namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public class GameServer
    {
        public ISubscriber publisher;
        readonly SemaphoreSlim operation_lock;
        public CancellationTokenSource cts;
        Task? logic_thread;
        public MapController map_controller;

        public GameServer()
        {
            this.publisher = Program.redis_connection._connection.GetSubscriber();
            this.operation_lock = new(1);
            this.cts = new();
            this.map_controller = new();
        }

        public void Start()
        {
            this.logic_thread = Task.Run(GameLoop, this.cts.Token);
        }

        async Task GameLoop()
        {
            while (!this.cts.Token.IsCancellationRequested)
            {
                try
                {
                    while (true)
                    {
                        byte[]? message = await Program.cache_helper.Dequeue("packet_queue");
                        if (message == null)
                        {
                            await Task.Delay(10);
                            continue;
                        }

                        Packet? packet = new(message);
                        if (packet != null)
                        {
                            await ProcessReceiveAsync(packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[GameServer] {e.Message}, {e.StackTrace}");
                }
            }

            try
            {
                this.logic_thread!.Wait();
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"[GameServer] {e.StackTrace} || {e.Message}");
            }
        }

        async Task HandleMessage<T>(long player_id, byte[] body, Func<long, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(player_id, msg);
        }

        async Task ProcessReceiveAsync(Packet packet)
        {
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            long player_id = packet.PopPlayerId();
            byte[] body = packet.PopBody();

            switch (protocol_id)
            {
                case PROTOCOL.C_TO_S_MOVE:
                    await HandleMessage<C_TO_S_MOVE>(player_id, body, MovePlayer);
                    break;

                case PROTOCOL.C_TO_S_PLAYER_INFO:
                    await HandleMessage<C_TO_S_PLAYER_INFO>(player_id, body, GetPlayerInfo);
                    break;

                case PROTOCOL.C_TO_S_OBJECT_INFO:
                    await HandleMessage<C_TO_S_OBJECT_INFO>(player_id, body, GetObjectInfo);
                    break;
            }
        }

        // TODO updatePosition 다른데로 옮길 것
        // public async Task HeartBeat(C_TO_S_HEART_BEAT msg)
        // {
        //     await UpdatePosition(msg.player_id);
        // }

        async Task UpdatePosition(long player_id)
        {
            PlayerInfo? player_info = await PlayerController.Load(player_id);
            if (player_info == null)
            {
                return;
            }

            GameObjectInfo object_info = player_info.object_info;
            if (object_info.target_cell.Equals(object_info.current_cell))
            {
                return;
            }

            // 아직 이동이 완료되지 않음
            if (object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
            {
                return;
            }

            // await user.player_lock.WaitAsync();
            await this.map_controller.MovePlayer(player_info, DirectionType.NONE);
            // user.player_lock.Release();
        }

        public async Task MovePlayer(long player_id, C_TO_S_MOVE msg)
        {
            try
            {
                Console.WriteLine("move");
                PlayerInfo? player_info = await PlayerController.Load(player_id);
                if (player_info == null)
                {
                    return;
                }

                // 아직 이동이 완료되지 않음
                if (player_info.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                {
                    return;
                }

                await this.map_controller.MovePlayer(player_info, msg.direction);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.StackTrace}, {e.Message}");
            }
        }

        public async Task GetPlayerInfo(long player_id, C_TO_S_PLAYER_INFO msg)
        {
            PlayerInfo? player_info = await PlayerController.Load(player_id);
            if (player_info == null)
            {
                return;
            }

            List<PlayerInfo> player_info_list = new();
            foreach (var target_player_id in msg.player_id_list)
            {
                PlayerInfo? target_player_info = await PlayerController.Load(target_player_id);
                if (target_player_info == null)
                {
                    continue;
                }

                player_info_list.Add(target_player_info);
            }

            for (int i = 0; i < player_info_list.Count; i += Config.BROADCAST_UNIT)
            {
                List<PlayerInfo> chunk = player_info_list
                    .Skip(i)
                    .Take(Config.BROADCAST_UNIT)
                    .ToList();

                _ = PublishToChannel(
                    $"object_info_{player_info.player_id}",
                    MessagePackSerializer.Serialize(chunk)
                );
            }
        }

        public async Task GetObjectInfo(long player_id, C_TO_S_OBJECT_INFO msg)
        {
            PlayerInfo? player_info = await PlayerController.Load(player_id);
            if (player_info == null)
            {
                return;
            }

            RedisValue[] request = msg.object_key_list.Select(key => (RedisValue)key).ToArray();

            foreach (var object_info in await GameObjecController.LoadAll(request))
            {
                _ = PublishToChannel(
                    $"object_{player_id}",
                    MessagePackSerializer.Serialize(object_info)
                );
            }
        }

        // TODO 접속종료 처리
        // public async Task LeavePlayer(long player_id, C_TO_S_LOGOUT msg)
        // {
        //     PlayerInfo? player_info = await PlayerController.Load(msg.player_id);
        //     if (player_info == null)
        //     {
        //         return;
        //     }

        //     // using (await LockHelper.AcquireLock(PlayerInfo.GetLockKey(user.player_info.player_id)))
        //     {
        //         await this.map_controller.UnsetPlayer(player_info.object_info);
        //         // await player_info.Delete();

        //         // TODO 시스템메시지 분리
        //     }
        // }

        public async Task PublishToChannel(string channel_name, RedisValue message)
        {
            RedisChannel channel = new(channel_name, RedisChannel.PatternMode.Literal);
            await this.publisher.PublishAsync(channel, message);
        }
    }
}
