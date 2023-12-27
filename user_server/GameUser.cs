#pragma warning disable IDE1006

namespace user_server
{
    using network;
    using MessagePack;
    using System.Collections.Concurrent;
    using StackExchange.Redis;

    public class GameUser : IPeer
    {
        ISubscriber operation_subscriber;
        ISubscriber object_subscriber;
        ISubscriber object_info_subscriber;
        public UserToken token { get; private set; }
        public SemaphoreSlim player_lock;
        public long player_id;

        // public PlayerInfo? player_info { get; set; }
        public CancellationTokenSource cts;
        public ConcurrentQueue<GameObjectInfo> object_info_queue;
        public ConcurrentQueue<List<PlayerInfo>> player_info_list_queue;
        public string? move_channel;
        Task? world_info_task;
        Task? game_object_subscribe_task;
        Task? game_object_info_task;

        public GameUser(UserToken token)
        {
            this.operation_subscriber = Program.redis_connection._connection.GetSubscriber();
            this.object_subscriber = Program.redis_connection._connection.GetSubscriber();
            this.object_info_subscriber = Program.redis_connection._connection.GetSubscriber();

            this.token = token;
            this.token.is_alive = true;
            this.token.is_released = false;
            this.token.SetPeer(this);

            this.player_lock = new(1);
            this.cts = new();

            this.object_info_queue = new();
            this.player_info_list_queue = new();

            this.move_channel = default;

            this.player_id = 0;

            this.world_info_task = Task.Run(RecvWorldInfo, cts.Token);
            this.game_object_subscribe_task = Task.Run(SubscribeObjectInfo, cts.Token);
            this.game_object_info_task = Task.Run(RecvObjectInfo, cts.Token);
        }

        // 일정 주기마다 브로드캐스트 범위 내의 오브젝트를 확인하는 Task
        async Task RecvWorldInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (this.player_id == 0)
                    {
                        continue;
                    }

                    GameObjectInfo? object_info = await LoadGameObject(
                        ObjectType.PLAYER,
                        this.player_id
                    );

                    if (object_info == null)
                    {
                        continue;
                    }

                    Cell current_cell = Cell.Clone(object_info.current_cell);
                    this.player_lock.Release();

                    List<string> bound_cell_list = MapHelper
                        .GetBoundCellList(current_cell)
                        .Select((cell) => MapHelper.GetPositionKey(object_info.map_id, cell))
                        .ToList();

                    RedisValue[] object_keys = await Program.cache_helper.ListRange(
                        bound_cell_list
                    );

                    var player_key = GameObjectInfo.MakeHashField(
                        ObjectType.PLAYER,
                        this.player_id
                    );

                    if (!object_keys.Contains(player_key))
                    {
                        continue;
                    }

                    for (int i = 0; i < object_keys.Length; i += Config.BROADCAST_UNIT)
                    {
                        RedisValue[] batch = object_keys
                            .Skip(i)
                            .Take(Config.BROADCAST_UNIT)
                            .ToArray();

                        var remain = object_keys.Length - i - Config.BROADCAST_UNIT;
                        var is_ended = remain <= 0;

                        Packet packet = PacketMaker.MakeMapInfoPacket(
                            batch.Select((item) => item.ToString()).ToList(),
                            is_ended
                        );

                        this.Send(packet);
                    }

                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
                    await this.OnRemoved();
                }
            }

            try
            {
                this.world_info_task!.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"[UserServer] {e.StackTrace} || {e.Message}");
            }
        }

        // 구독중인 Cell에 오는 메시지를 취합하는 Task
        async Task SubscribeObjectInfo()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine("11111");
                    if (player_id == 0)
                    {
                        continue;
                    }

                    // object_info_queue 데이터가 없을 때까지 기다리고 비동기적으로 처리
                    while (!this.object_info_queue.Any())
                    {
                        await Task.Delay(100);
                    }

                    List<GameObjectInfo> game_object_list = new();

                    // object_info_queue 데이터가 있을 때 리스트에 추가
                    while (this.object_info_queue.TryDequeue(out var object_info))
                    {
                        if (game_object_list.Count >= Config.BROADCAST_UNIT)
                        {
                            break;
                        }

                        game_object_list.Add(object_info);
                    }

                    Packet packet = PacketMaker.MakeMapUpdatePacket(game_object_list);
                    this.Send(packet);
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
                Console.WriteLine("22222");
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
                        Packet player_info_packet = PacketMaker.MakePlayerInfoPacket(info_list);
                        this.Send(player_info_packet);
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

        public async Task OnMessage(Const<byte[]> buffer)
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

                    case PROTOCOL.C_TO_S_LOGIN:
                        await this.player_lock.WaitAsync();
                        await HandleMessage<C_TO_S_LOGIN>(player_id, packet.PopBody(), Login);
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
                        await Program.cache_helper.Enqueue("packet_queue", clone);
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
        public async Task ProcessUserOperation(RedisValue message)
        {
            try
            {
                await this.player_lock.WaitAsync();

                var packet = MessagePackSerializer.Deserialize<Packet>(message);

                PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();

                long player_id = packet.PopPlayerId();
                byte[] body = packet.PopBody();

                switch (protocol_id)
                {
                    case PROTOCOL.C_TO_S_MOVE:
                        await HandleMessage<C_TO_S_MOVE>(player_id, body, Move);
                        break;

                    case PROTOCOL.C_TO_S_PLAYER_INFO:
                        await HandleMessage<C_TO_S_PLAYER_INFO>(player_id, body, GetPlayerInfo);
                        break;

                    case PROTOCOL.C_TO_S_OBJECT_INFO:
                        await HandleMessage<C_TO_S_OBJECT_INFO>(player_id, body, GetObjectInfo);
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

        async Task HandleMessage<T>(long player_id, byte[] body, Func<long, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(player_id, msg);
        }

        void HeartBeat()
        {
            this.token.is_alive = true;
        }

        async Task Login(long _, C_TO_S_LOGIN request)
        {
            if (this.player_id != 0)
            {
                throw new Exception("Already Has Player id");
            }

            // TODO 로그인용 웹서버
            long temp_player_id = await Program.cache_helper.StringIncrement("temp_player_id");
            if (temp_player_id < 0)
            {
                // TODO 이미 로그인된 유저인지 확인 로직 추가할 것
                throw new Exception("Already Exist Player");
            }

            PlayerInfo player_info;
            using (await Program.lock_helper.AcquireLock(PlayerInfo.GetLockKey(temp_player_id)))
            {
                // 플레이어 생성
                player_info = new(
                    temp_player_id,
                    name: $"플레이어{temp_player_id}",
                    MapHelper.GetRandomCell()
                );

                player_info.object_info.map_id = 1; // TODO 임시

                await Program.cache_helper.HashSet(
                    GameObjectInfo.HASH_KEY,
                    player_info.object_info.GetHashField(),
                    MessagePackSerializer.Serialize(player_info.object_info)
                );

                await Program.cache_helper.HashSet(
                    PlayerInfo.HASH_KEY,
                    player_info.player_id,
                    MessagePackSerializer.Serialize(player_info)
                );

                this.player_id = player_info.player_id;
            }

            // 계정 정보 전송
            Packet login_packet = PacketMaker.MakeLoginPacket(player_info);
            this.Send(login_packet);

            await StartSubscribe();

            // await Program.user_server.MovePlayer(this, DirectionType.NONE);
        }

        async Task StartSubscribe()
        {
            await this.operation_subscriber.SubscribeAsync(
                new($"operation_{this.player_id}", RedisChannel.PatternMode.Literal),
                async (channel, message) => await ProcessUserOperation(message)
            );

            await this.object_subscriber.SubscribeAsync(
                new($"object_{this.player_id}", RedisChannel.PatternMode.Literal),
                (channel, message) =>
                    this.object_info_queue.Enqueue(
                        MessagePackSerializer.Deserialize<GameObjectInfo>(message)
                    )
            );

            await this.object_info_subscriber.SubscribeAsync(
                new($"object_info_{this.player_id}", RedisChannel.PatternMode.Literal),
                (channel, message) =>
                    this.player_info_list_queue.Enqueue(
                        MessagePackSerializer.Deserialize<List<PlayerInfo>>(message)
                    )
            );
        }

        async Task ReleaseSubscribe()
        {
            await this.operation_subscriber.UnsubscribeAllAsync();
            await this.object_subscriber.UnsubscribeAllAsync();
            await this.object_info_subscriber.UnsubscribeAllAsync();
        }

        async Task Move(long player_id, C_TO_S_MOVE request)
        {
            // await Program.user_server.MovePlayer(this, request.direction);
        }

        async Task GetPlayerInfo(long player_id, C_TO_S_PLAYER_INFO request)
        {
            // await Program.user_server.GetPlayerInfo(this, request.player_id_list);
        }

        async Task GetObjectInfo(long player_id, C_TO_S_OBJECT_INFO request)
        {
            // await Program.user_server.GetObjectInfo(this, request.object_key_list);
        }

        public static async Task<GameObjectInfo?> LoadGameObject(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await Program.cache_helper.HashGet(
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

        public void Send(Packet msg)
        {
            this.token.Send(msg);
            Packet.Destroy(msg);
        }

        public async Task OnRemoved()
        {
            // this.cts.Cancel();

            // TODO game_server에서 처리 후 후처리 (ex: 맵에 있는 유저 키 삭제 등)
            // await Program.user_server.LeavePlayer(this);
            await Program.cache_helper.HashDelete(GameObjectInfo.HASH_KEY, this.player_id);
            await Program.cache_helper.HashDelete(PlayerInfo.HASH_KEY, this.player_id);
            this.player_id = 0;

            await ReleaseSubscribe();

            Console.WriteLine("[UserServer] The client disconnected.");
        }
    }
}
