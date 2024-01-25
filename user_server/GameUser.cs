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

        RedisConnection redis_connection;
        public CacheHelper cache_helper { get; private set; }
        public RedLockFactory redlock { get; private set; }

        ISubscriber game_server_subscriber;

        Task game_object_subscribe_task;
        public CancellationTokenSource cts;
        public ConcurrentQueue<string> map_queue;
        public ConcurrentQueue<GameObjectInfo> move_object_queue;
        public SemaphoreSlim player_lock;

#pragma warning disable CS8618
        public GameUser(UserToken token)
        {
            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_id = 0;
            this.player_lock = new(1);

            this.cts = new();
            this.move_object_queue = new();

            this.initializeAsync().Wait();
        }
#pragma warning restore

        public async Task initializeAsync()
        {
            this.redis_connection = await RedisConnection.InitializeAsync();
            this.redlock = this.redis_connection.GetRedLockFactory();
            this.cache_helper = new(this.redis_connection);

            this.game_server_subscriber = this.redis_connection._connection.GetSubscriber();
            this.game_object_subscribe_task = Task.Run(SubscribeMove, cts.Token);
        }

        public async Task ReleaseAsync()
        {
            Packet packet = PacketMaker.U_TO_G_LOGOUT(this.player_id);
            _ = this.SendToGameServer(packet);

            this.player_id = 0;
            await this.game_server_subscriber.UnsubscribeAllAsync();
            this.redis_connection.Dispose();
        }

        // 구독중인 Cell에 오는 Move 메시지를 취합하는 Task (오로지 모아서 보내는 목적)
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
                    LogManager.WriteErrorLog(e);
                    this.OnRemoved();
                }
            }

            try
            {
                this.game_object_subscribe_task!.Wait();
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

                // 단순 조회 or 브로드캐스팅 없는 유저 단일 로직이라면 유저 서버 내부에서 처리
                // 그 외에는 게임 서버 전송
                switch (protocol_id)
                {
                    case PROTOCOL.HEART_BEAT:
                        HeartBeat();
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

                            default:
                                await this.SendToGameServer(packet);
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

        public async Task OnMessageFromGameServer(RedisValue message)
        {
            try
            {
                await this.player_lock.WaitAsync();

                Packet packet = new((byte[])message!, this);
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

            bool is_created = false;

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
                is_created = true;
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

            // LogManager.WriteLoginLog(LoginType.GUEST, $"{temp_player_id}", player_info, is_created);
        }

        async Task RequestMove(long player_id, C_TO_U_MOVE body)
        {
            if (player_id != this.player_id)
            {
                return;
            }

            LogManager.WriteDebugLog("request move from client");

            GameObjectInfo? object_info = await GameObjectController.Load(
                this.cache_helper,
                ObjectType.PLAYER,
                player_id
            );

            if (object_info == null)
            {
                return;
            }

            // 아직 이동이 완료되지 않음
            if (object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            LogManager.WriteDebugLog("request move to game server");
            Packet packet = PacketMaker.U_TO_G_MOVE(player_id, body.direction);
            await SendToGameServer(packet);
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

        // 이 함수가 호출되는 경우: G_TO_U_MAP_INFO의 object_key_list에는 있으나 클라에는 GameObjectInfo가 없을 때
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

#pragma warning disable CS1998
        async Task Move(long _, G_TO_U_MOVE body)
        {
            this.move_object_queue.Enqueue(body.object_info);
        }
#pragma warning restore CS1998


#pragma warning disable CS1998
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
                LogManager.WriteErrorLog(e);
                this.OnRemoved();
            }
        }
#pragma warning restore CS1998

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

        public void OnRemoved()
        {
            this.cts!.Cancel();
            this.cts.Dispose();

            Program.leave_user_queue!.Enqueue(this);
        }
    }
}
