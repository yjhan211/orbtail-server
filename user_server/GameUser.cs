#pragma warning disable IDE1006

namespace user_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;
    using StackExchange.Redis;
    using game_server;
    using RedLockNet.SERedis;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public long player_id;
        Cell last_cell;
        GameObjectInfo object_info;
        RedisConnection redis_connection;
        NatsClient nats_client;
        public CacheHelper cache_helper { get; private set; }
        public RedLockFactory redlock { get; private set; }

        Task move_object_task;
        public CancellationTokenSource cts;

        public ConcurrentQueue<string> map_queue;
        public ConcurrentQueue<GameObjectInfo> move_object_queue;
        public SemaphoreSlim player_lock;
        SemaphoreSlim object_lock;

#pragma warning disable CS8618
        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_id = 0;
            this.player_lock = new(1);
            this.object_lock = new(1);

            this.cts = new();
            this.move_object_queue = new();

            this.initializeAsync().Wait();
        }
#pragma warning restore
#pragma warning disable CS1998

        public async Task initializeAsync()
        {
            this.redis_connection = await RedisConnection.InitializeAsync();
            this.redlock = this.redis_connection.GetRedLockFactory();
            this.cache_helper = new(this.redis_connection);
            this.nats_client = new(Program.nats_endpoint);

            this.move_object_task = Task.Run(RecvMoveObjectTask, cts.Token);
        }

        public void Release()
        {
            PublishDestroy();

            Packet packet = PacketMaker.U_TO_G_LOGOUT(this.player_id);
            _ = this.SendToGameServer(packet);

            this.player_id = 0;
            this.nats_client.Close();
            this.redis_connection.Dispose();
        }

        // 구독중인 Cell에 오는 Move 메시지를 취합하는 Task (오로지 모아서 보내는 목적)
        async Task RecvMoveObjectTask()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_id == 0)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    List<GameObjectInfo> game_object_list = new();

                    while (this.move_object_queue.TryDequeue(out var object_info))
                    {
                        if (game_object_list.Count >= Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        game_object_list.Add(object_info);
                    }

                    if (game_object_list.Count > 0)
                    {
                        Packet packet = PacketMaker.U_TO_C_MAP_UPDATE(game_object_list);
                        this.SendToClient(packet);
                    }

                    await Task.Delay(100);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                    this.OnRemoved();
                }
            }

            try
            {
                this.move_object_task!.Wait();
            }
            catch (AggregateException e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public async Task OnMessageFromClient(Const<byte[]> buffer)
        {
            try
            {
                byte[] clone = new byte[Config.BUFFER_SIZE];
                Array.Copy(buffer.Value, clone, buffer.Value.Length);

                Packet packet = new(clone, this);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                byte[] body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.HEART_BEAT:
                        await HeartBeat();
                        break;

                    case PROTOCOL.C_TO_U_LOGIN:
                        await this.player_lock.WaitAsync();
                        await HandleMessage<C_TO_U_LOGIN>(player_id, body, Login);
                        break;

                    default:
                        if (player_id == 0 || this.player_id != player_id)
                        {
                            throw new Exception($"Invalid ID: {this.player_id}, {player_id}");
                        }
                        switch (protocol_id)
                        {
                            case PROTOCOL.C_TO_U_MOVE:
                                await HandleMessage<C_TO_U_MOVE>(player_id, body, RequestMove);
                                break;

                            case PROTOCOL.C_TO_U_PLAYER_INFO:
                                await HandleMessage<C_TO_U_PLAYER_INFO>(
                                    player_id,
                                    body,
                                    GetPlayerInfo
                                );
                                break;

                            case PROTOCOL.C_TO_U_OBJECT_INFO:
                                await HandleMessage<C_TO_U_OBJECT_INFO>(
                                    player_id,
                                    body,
                                    GetObjectInfo
                                );
                                break;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
            finally
            {
                this.player_lock.Release();
            }
        }

        public async Task SubscribeGameServer(RedisValue message)
        {
            try
            {
                await this.player_lock.WaitAsync();

                Packet packet = new((byte[])message!);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                var body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.G_TO_U_MOVE:
                        await HandleMessage<G_TO_U_MOVE>(player_id, body, SubscribeMove);
                        break;

                    case PROTOCOL.G_TO_U_SPAWN:
                        await HandleMessage<G_TO_U_SPAWN>(player_id, body, SubscribeSpawn);
                        break;

                    case PROTOCOL.G_TO_U_DESTROY:
                        await HandleMessage<G_TO_U_DESTROY>(player_id, body, SubscribeDestroy);
                        break;
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
            finally
            {
                this.player_lock.Release();
            }
        }

        async Task HandleMessage<T>(long player_id, byte[] body, Func<long, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(player_id, msg);
        }

        async Task HeartBeat()
        {
            this.token.is_alive = true;

            if (this.object_info == null)
            {
                return;
            }

            if (!this.object_info.current_cell.Equals(this.object_info.target_cell))
            {
                if (this.object_info.GetMoveElapsedTime() >= Config.MOVE_ELAPSED_TIME)
                {
                    await Move(this.object_info.target_cell, DirectionType.NONE);
                }
            }
        }

        async Task Login(long _, C_TO_U_LOGIN request)
        {
            if (this.player_id != 0)
            {
                throw new Exception("Already Has Player id");
            }

            // TODO 로그인용 웹서버
            long temp_player_id = await this.cache_helper.StringIncrement("temp_player_id");
            if (temp_player_id < 0)
            {
                // TODO 이미 로그인된 유저인지 확인 로직 추가할 것
                throw new Exception("Already Exist Player");
            }

            PlayerInfo player_info;
            using (var player_lock = await PlayerController.Lock(this.redlock, this.player_id))
            {
                // 플레이어 생성
                player_info = new(
                    temp_player_id,
                    name: $"플레이어{temp_player_id}",
                    MapHelper.GetRandomCell()
                );

                player_info.object_info.map_id = 1; // TODO 임시
                await PlayerController.Save(this.cache_helper, player_info);

                this.player_id = player_info.player_id;
            }

            this.object_info = player_info.object_info;
            this.last_cell = Cell.Clone(player_info.object_info.current_cell);

            // 게임 서버 구독 시작
            this.nats_client.Subscribe(
                this.object_info.GetHashField(),
                async (channel, message) => await SubscribeGameServer(message)
            );

            // 계정 정보 전송
            Packet login_packet = PacketMaker.U_TO_C_LOGIN(player_info);
            SendToClient(login_packet);

            await Move(this.object_info.current_cell, DirectionType.NONE, true);
        }

        async Task RequestMove(long player_id, C_TO_U_MOVE body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            if (this.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                // 아직 이동이 완료되지 않음
                return;
            }

            var next_target_cell = MapHelper.CalcTargetCell(
                this.object_info.target_cell,
                body.direction
            );

            if (MapHelper.IsOutOfMapRange(next_target_cell))
            {
                // 맵 밖으로 벗어남
                return;
            }

            await Move(next_target_cell, body.direction);
        }

        async Task Move(Cell next_target_cell, DirectionType direction, bool all_bound = false)
        {
            await this.object_lock.WaitAsync();

            var current_manage_server = GetObjectManageServer(this.object_info.current_cell);
            var target_manage_server = GetObjectManageServer(this.object_info.target_cell);

            // 과거 위치 챙겨놓고
            this.last_cell = Cell.Clone(this.object_info.current_cell);

            // current_cell을 target_cell로 변경
            this.object_info.current_cell = Cell.Clone(this.object_info.target_cell);

            // target_cell을 새로운 target_cell로 변경 및 move_timestamp 업데이트
            this.object_info.move_timestamp = DateTime.UtcNow;
            this.object_info.target_cell = next_target_cell;
            if (direction != DirectionType.NONE)
            {
                this.object_info.SetFlip(direction);
            }

            this.object_lock.Release();

            if (current_manage_server != target_manage_server)
            {
                // 과거 담당 서버에는 영역을 떠났다고 전송
                PublishLeave();
            }

            // 현재 담당 서버에 전송
            PublishMove();

            var last_bound_cell_list = all_bound
                ? new()
                : MapHelper.GetBoundCellList(this.last_cell);

            var current_bound_cell_list = MapHelper.GetBoundCellList(this.object_info.current_cell);

            // 현재 바운드 - 이전 바운드 = spawn 대상
            var object_spawn_list = current_bound_cell_list
                .Except(last_bound_cell_list)
                .GroupBy(
                    cell => MapHelper.CalcServerIdFromCell(cell, Program.game_server_num),
                    cell => MapHelper.GetPositionKey(cell)
                )
                .Select(group => new { server_id = group.Key, position_key_list = group.ToList() });

            foreach (var item in object_spawn_list)
            {
                RequestSpawnObjectList(item.server_id, item.position_key_list);
            }
        }

        void RequestSpawnObjectList(int server_id, List<string> position_key_list)
        {
            this.nats_client.Publish(
                $"spawn_object_{server_id}",
                MessagePackSerializer.Serialize(
                    (this.object_info.GetHashField(), position_key_list)
                )
            );
        }

        async Task GetPlayerInfo(long player_id, C_TO_U_PLAYER_INFO body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            var player_id_list = body.player_id_list;
            var player_info_list = new List<PlayerInfo>();
            for (int i = 0; i < player_id_list.Count; i++)
            {
                var target_player_id = player_id_list[i];
                using (var player_lock = await PlayerController.Lock(this.redlock, this.player_id))
                {
                    var target_player_info = await PlayerController.Load(
                        this.cache_helper,
                        target_player_id
                    );

                    if (target_player_info == null)
                    {
                        continue;
                    }

                    player_info_list.Add(target_player_info);

                    bool is_max = player_info_list.Count >= Config.BROADCAST_UNIT;
                    bool is_ended = i == player_id_list.Count - 1;

                    if (is_max || is_ended)
                    {
                        Packet packet = PacketMaker.U_TO_C_PLAYER_INFO(player_info_list);
                        this.SendToClient(packet);

                        await Task.Delay(100);
                    }
                }
            }
        }

        // 이 함수가 호출되는 경우: G_TO_U_SPAWN_LIST의 object_key_list에는 있으나 클라에는 GameObjectInfo가 없을 때
        // 어떤 경우에 생기는가: 이미 접속해서 잠수타고 있는 오브젝트를 만났을 때
        async Task GetObjectInfo(long player_id, C_TO_U_OBJECT_INFO body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            RedisValue[] keys = body.object_key_list.ConvertAll(x => (RedisValue)x).ToArray();
            var object_info_list = await GameObjectController.LoadAll(this.cache_helper, keys);
            foreach (var object_info in object_info_list)
            {
                this.move_object_queue.Enqueue(object_info);
            }
        }

        async Task SubscribeMove(long _, G_TO_U_MOVE body)
        {
            this.move_object_queue.Enqueue(body.object_info);
        }

        async Task SubscribeSpawn(long _, G_TO_U_SPAWN body)
        {
            try
            {
                var object_keys = body.object_key_list;
                var player_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, this.player_id);

                for (int i = 0; i < object_keys.Count; i += Config.BROADCAST_UNIT)
                {
                    List<string> batch = object_keys.Skip(i).Take(Config.BROADCAST_UNIT).ToList();

                    var remain = object_keys.Count - i - Config.BROADCAST_UNIT;
                    var is_ended = remain <= 0;

                    Packet packet = PacketMaker.U_TO_C_SPAWN(
                        batch.Select((item) => item.ToString()).ToList(),
                        is_ended
                    );

                    this.SendToClient(packet);
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }

        async Task SubscribeDestroy(long _, G_TO_U_DESTROY body)
        {
            try
            {
                var object_key = body.object_key;

                Packet packet = PacketMaker.U_TO_C_DESTROY(object_key);
                this.SendToClient(packet);

                LogManager.WriteInfoLog(object_key);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }

#pragma warning restore CS1998

        public int GetObjectManageServer(Cell cell)
        {
            return MapHelper.CalcServerIdFromCell(cell, Program.game_server_num);
        }

        public void SendToClient(Packet msg)
        {
            this.token.Send(msg);
            Packet.Destroy(msg);
        }

        public async Task SendToGameServer(Packet msg)
        {
            await this.cache_helper.Enqueue("game_server_queue", msg.ToBytes());
            Packet.Destroy(msg);
        }

        public void PublishLeave(Cell? leave_cell = null)
        {
            if (leave_cell == null)
            {
                leave_cell = this.last_cell;
            }

            var manage_server = GetObjectManageServer(leave_cell);
            this.nats_client.Publish(
                $"leave_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (MapHelper.GetPositionKey(leave_cell), this.object_info.GetHashField())
                )
            );
        }

        public void PublishDestroy()
        {
            var manage_server = GetObjectManageServer(this.object_info.current_cell);
            this.nats_client.Publish(
                $"destroy_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (
                        MapHelper.GetPositionKey(this.object_info.current_cell),
                        this.object_info.GetHashField()
                    )
                )
            );
        }

        public void PublishMove()
        {
            var manage_server = GetObjectManageServer(this.object_info.current_cell);
            this.nats_client.Publish(
                $"move_object_{manage_server}",
                MessagePackSerializer.Serialize(
                    (MapHelper.GetPositionKey(this.last_cell), this.object_info)
                )
            );
        }

        public void OnRemoved()
        {
            this.cts!.Cancel();
            this.cts.Dispose();

            Program.leave_user_queue!.Enqueue(this);
        }
    }
}
