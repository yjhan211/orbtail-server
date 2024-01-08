namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;
    using RedLockNet.SERedis;
    using StackExchange.Redis;

    public class GameServer
    {
        public CancellationTokenSource cts;
        Task? logic_thread;
        public MapController map_controller;

        public GameServer()
        {
            this.cts = new();
            this.map_controller = new();
        }

        public void Start(int server_id)
        {
            this.map_controller.Initialize(server_id);
            this.logic_thread = Task.Run(GameLoop, this.cts.Token);
        }

        async Task GameLoop()
        {
            while (!this.cts.Token.IsCancellationRequested)
            {
                try
                {
                    var redis_connection = await RedisConnection.InitializeAsync();
                    CacheHelper cache_helper = new(redis_connection);
                    while (true)
                    {
                        byte[]? message = await cache_helper.Dequeue("packet_queue");
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

        async Task HandleMessage<T>(
            RedisConnection redis_conn,
            long player_id,
            byte[] body,
            Func<RedisConnection, long, T, Task> handleMessage
        )
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(redis_conn, player_id, msg);
        }

        async Task ProcessReceiveAsync(Packet packet)
        {
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            long player_id = packet.PopPlayerId();
            byte[] body = packet.PopBody();

            using (var redis_conn = await RedisConnection.InitializeAsync())
            {
                switch (protocol_id)
                {
                    case PROTOCOL.U_TO_G_MOVE:
                        await HandleMessage<U_TO_G_MOVE>(redis_conn, player_id, body, MovePlayer);
                        break;

                    case PROTOCOL.U_TO_G_LOGOUT:
                        await HandleMessage<U_TO_G_LOGOUT>(redis_conn, player_id, body, Logout);
                        break;
                }
            }
        }

        async Task UpdatePosition(RedisConnection redis_conn, long player_id)
        {
            CacheHelper cache_helper = new(redis_conn);
            PlayerInfo? player_info = await PlayerController.Load(cache_helper, player_id);
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

            // await user.player_lock.WaitAsync()
            await this.map_controller.MovePlayer(cache_helper, player_info, DirectionType.NONE);
            // user.player_lock.Release();
        }

        public async Task MovePlayer(RedisConnection redis_conn, long player_id, U_TO_G_MOVE msg)
        {
            try
            {
                CacheHelper cache_helper = new(redis_conn);
                PlayerInfo? player_info = await PlayerController.Load(cache_helper, player_id);
                if (player_info == null)
                {
                    return;
                }

                await this.map_controller.MovePlayer(cache_helper, player_info, msg.direction);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.StackTrace}, {e.Message}");
            }
        }

        public async Task Logout(RedisConnection redis_conn, long player_id, U_TO_G_LOGOUT msg)
        {
            try
            {
                CacheHelper cache_helper = new(redis_conn);
                var redlock = redis_conn.GetRedLockFactory();

                using (var player_lock = await PlayerController.Lock(redlock, player_id))
                {
                    PlayerInfo? player_info = await PlayerController.Load(
                        cache_helper,
                        msg.player_id
                    );

                    if (player_info == null)
                    {
                        return;
                    }

                    GameObjectInfo object_info = player_info.object_info;
                    if (object_info == null)
                    {
                        return;
                    }

                    await cache_helper.ListRemove(
                        MapHelper.GetPositionKey(object_info.map_id, object_info.current_cell),
                        object_info.GetHashField()
                    );

                    await cache_helper.ListRemove(
                        MapHelper.GetPositionKey(object_info.map_id, object_info.target_cell),
                        object_info.GetHashField()
                    );

                    await PlayerController.Delete(cache_helper, player_id);
                }

                // TODO 이 처리 이후 큐로 패킷이 온다면 DB에서 처리한다.
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.StackTrace}, {e.Message}");
            }
        }

        // 주의: 여러 개의 채널에 하나의 패킷 전송 시 반드시 PublishToChannels 이용. Destroy 때문...
        public async Task PublishToChannel(
            ISubscriber publisher,
            string channel_name,
            Packet packet
        )
        {
            RedisChannel channel = new(channel_name, RedisChannel.PatternMode.Literal);
            _ = publisher.PublishAsync(channel, packet.ToBytes());

            Packet.Destroy(packet);
        }

        public async Task PublishToChannels(
            ISubscriber publisher,
            List<string> channel_name_list,
            Packet packet
        )
        {
            foreach (var channel_name in channel_name_list)
            {
                RedisChannel channel = new(channel_name, RedisChannel.PatternMode.Literal);
                _ = publisher.PublishAsync(channel, packet.ToBytes());
            }

            Packet.Destroy(packet);
        }
    }
}
