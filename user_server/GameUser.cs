#pragma warning disable IDE1006

namespace user_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;
    using StackExchange.Redis;

    public class GameUser : IPeer
    {
        public UserToken token { get; private set; }
        public long player_id;

        RedisConnection redis_connection;
        CacheHelper cache_helper;
        LockHelper lock_helper;

        ISubscriber game_server_subscriber;

        Task game_object_subscribe_task;
        Task game_object_info_task;

        public CancellationTokenSource cts;
        public ConcurrentQueue<string> map_queue;
        public ConcurrentQueue<GameObjectInfo> move_object_queue;
        public ConcurrentQueue<List<PlayerInfo>> player_info_list_queue;

        public SemaphoreSlim player_lock;

#pragma warning disable CS8618
        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_lock = new(1);
            this.cts = new();

            this.move_object_queue = new();
            this.player_info_list_queue = new();

            this.player_id = 0;

            this.initializeAsync().Wait();
        }
#pragma warning restore

        public async Task initializeAsync()
        {
            this.redis_connection = await RedisConnection.InitializeAsync();

            this.cache_helper = new(this.redis_connection);
            this.lock_helper = new(this.redis_connection);

            this.game_server_subscriber = this.redis_connection._connection.GetSubscriber();

            this.game_object_subscribe_task = Task.Run(SubscribeMove, cts.Token);
            this.game_object_info_task = Task.Run(RecvObjectInfo, cts.Token);
        }

        public async Task ReleaseAsync()
        {
            await this.game_server_subscriber.UnsubscribeAllAsync();
        }

        // 구독중인 Cell에 오는 Move 메시지를 취합하는 Task (모아서 보내려고)
        async Task SubscribeMove()
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
                    Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.game_object_subscribe_task!.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
            }
        }

        // 요청에 의한 응답으로 온 ObjectInfo를 취합하는 Task
        async Task RecvObjectInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (player_id == 0)
                    {
                        continue;
                    }

                    if (this.player_info_list_queue.TryDequeue(out List<PlayerInfo>? info_list))
                    {
                        Packet player_info_packet = PacketMaker.U_TO_C_PLAYER_INFO(info_list);
                        this.SendToClient(player_info_packet);
                    }

                    await Task.Delay(500);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.game_object_info_task!.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
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

                switch (protocol_id)
                {
                    // 게임이 아니라 유저 단일에만 영향을 미치는 로직이라면 유저서버 내부에서 처리
                    case PROTOCOL.HEART_BEAT:
                        HeartBeat();
                        break;

                    case PROTOCOL.C_TO_U_LOGIN:
                        await this.player_lock.WaitAsync();
                        await HandleMessage<C_TO_U_LOGIN>(player_id, packet.PopBody(), Login);
                        break;

                    case PROTOCOL.C_TO_U_MOVE:
                        await HandleMessage<C_TO_U_MOVE>(player_id, packet.PopBody(), RequestMove);
                        break;

                    default:
                        // 클라이언트 패킷은 기본적으로 게임 서버로 전송됨
                        if (player_id == 0)
                        {
                            return;
                        }
                        if (this.player_id != player_id)
                        {
                            throw new Exception(
                                $"Invalid Player ID: {this.player_id}, {player_id}"
                            );
                        }
                        await this.cache_helper.Enqueue("packet_queue", clone);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[UserServer] {e.Message}, {e.StackTrace}");
            }
            finally
            {
                this.player_lock.Release();
            }
        }

        // OnMessage -> GameServer -> this.subscriber -> ProgessUserOperation
        public async Task OnMessageFromGameServer(RedisValue message)
        {
            try
            {
                await this.player_lock.WaitAsync();

                byte[] clone = (byte[])message!;

                Packet packet = new(clone, this);
                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
                long player_id = packet.PopPlayerId();
                var body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.G_TO_U_MAP_INFO:
                        await HandleMessage<G_TO_U_MAP_INFO>(player_id, body, MapInfo);
                        break;

                    case PROTOCOL.G_TO_U_MOVE:
                        await HandleMessage<G_TO_U_MOVE>(player_id, body, Move);
                        break;

                    // case PROTOCOL.C_TO_U_PLAYER_INFO:
                    //     await HandleMessage<C_TO_U_PLAYER_INFO>(player_id, body, GetPlayerInfo);
                    //     break;

                    // case PROTOCOL.C_TO_U_OBJECT_INFO:
                    //     await HandleMessage<C_TO_U_OBJECT_INFO>(player_id, body, GetObjectInfo);
                    //     break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[UserServer] {e.Message}, {e.StackTrace}");
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

        void HeartBeat()
        {
            this.token.is_alive = true;
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
            using (await this.lock_helper.AcquireLock(PlayerInfo.GetLockKey(temp_player_id)))
            {
                // 플레이어 생성
                player_info = new(
                    temp_player_id,
                    name: $"플레이어{temp_player_id}",
                    MapHelper.GetRandomCell()
                );

                player_info.object_info.map_id = 1; // TODO 임시

                await this.cache_helper.HashSet(
                    GameObjectInfo.HASH_KEY,
                    player_info.object_info.GetHashField(),
                    MessagePackSerializer.Serialize(player_info.object_info)
                );

                await this.cache_helper.HashSet(
                    PlayerInfo.HASH_KEY,
                    player_info.player_id,
                    MessagePackSerializer.Serialize(player_info)
                );

                this.player_id = player_info.player_id;
            }

            // 계정 정보 전송
            Packet login_packet = PacketMaker.U_TO_C_LOGIN(player_info);
            SendToClient(login_packet);

            // 게임서버에 유저 정보 전송
            Packet move_packet = PacketMaker.U_TO_G_MOVE(this.player_id, DirectionType.NONE);
            await SendToGameServer(move_packet);

            // 게임 서버 구독 시작
            await this.game_server_subscriber.SubscribeAsync(
                new(
                    GameObjectInfo.MakeHashField(ObjectType.PLAYER, this.player_id),
                    RedisChannel.PatternMode.Literal
                ),
                async (channel, message) => await OnMessageFromGameServer(message)
            );
        }

        async Task RequestMove(long player_id, C_TO_U_MOVE body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            GameObjectInfo? object_info = await LoadGameObject(ObjectType.PLAYER, player_id);
            if (object_info == null)
            {
                return;
            }

            // 아직 이동이 완료되지 않음
            if (object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            Packet packet = PacketMaker.U_TO_G_MOVE(player_id, body.direction);
            await SendToGameServer(packet);
        }

        async Task Move(long _, G_TO_U_MOVE body)
        {
            this.move_object_queue.Enqueue(body.object_info);
        }

        async Task MapInfo(long _, G_TO_U_MAP_INFO body)
        {
            try
            {
                var object_keys = body.object_key_list;
                var player_key = GameObjectInfo.MakeHashField(ObjectType.PLAYER, this.player_id);

                if (!object_keys.Contains(player_key))
                {
                    return;
                }

                for (int i = 0; i < object_keys.Count; i += Config.BROADCAST_UNIT)
                {
                    List<string> batch = object_keys.Skip(i).Take(Config.BROADCAST_UNIT).ToList();

                    var remain = object_keys.Count - i - Config.BROADCAST_UNIT;
                    var is_ended = remain <= 0;

                    Packet packet = PacketMaker.U_TO_C_MAP_INFO(
                        batch.Select((item) => item.ToString()).ToList(),
                        is_ended
                    );

                    this.SendToClient(packet);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                await this.OnRemoved();
            }
        }

        public async Task<GameObjectInfo?> LoadGameObject(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await this.cache_helper.HashGet(
                    GameObjectInfo.HASH_KEY,
                    GameObjectInfo.MakeHashField(type, object_id)
                );

                if (serialized_data.IsNull)
                {
                    return null;
                }

                var object_info = MessagePackSerializer.Deserialize<GameObjectInfo?>(
                    serialized_data
                );

                return object_info;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace}{e.Message}");
                return null;
            }
        }

        public void SendToClient(Packet msg)
        {
            this.token.Send(msg);
            Packet.Destroy(msg);
        }

        public async Task SendToGameServer(Packet msg)
        {
            await this.cache_helper.Enqueue("packet_queue", msg.ToBytes());
            Packet.Destroy(msg);
        }

        public async Task OnRemoved()
        {
            this.cts!.Cancel();
            this.cts.Dispose();

            // TODO game_server에서 처리 후 후처리 (ex: 맵에 있는 유저 키 삭제 등)
            // await Program.user_server.LeavePlayer(this);

            await this.cache_helper.HashDelete(
                GameObjectInfo.HASH_KEY,
                GameObjectInfo.MakeHashField(ObjectType.PLAYER, this.player_id)
            );

            await this.cache_helper.HashDelete(PlayerInfo.HASH_KEY, this.player_id);

            this.player_id = 0;

            await ReleaseAsync();

            this.redis_connection.Dispose();

            Console.WriteLine("[UserServer] The client disconnected.");
        }
    }
}
