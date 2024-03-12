namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public class GameServer
    {
        NatsClient nats_client;
        CancellationTokenSource cts;
        Task? logic_thread;
        MapController map_controller;
        ConnectionMultiplexer redis_connection;
        CacheHelper cache_helper;

        public GameServer()
        {
            this.nats_client = new(Program.nats_endpoint);
            this.cts = new();
            this.map_controller = new();

            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new(this.redis_connection);
        }

        public void Start()
        {
            this.map_controller.Initialize(this.nats_client);
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
                        byte[]? message = await cache_helper.Dequeue("game_server_queue");
                        if (message == null)
                        {
                            await Task.Delay(10);
                            continue;
                        }

                        Packet? packet = new(message);
                        if (packet != null)
                        {
                            await ProcessReceiveAsync(cache_helper, packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            }

            try
            {
                this.logic_thread!.Wait();
            }
            catch (AggregateException e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        async Task HandleMessage<T>(
            CacheHelper cache_helper,
            long player_id,
            byte[] body,
            Func<CacheHelper, long, T, Task> handleMessage
        )
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(cache_helper, player_id, msg);
        }

        async Task ProcessReceiveAsync(CacheHelper cache_helper, Packet packet)
        {
            PROTOCOL protocol_id = (PROTOCOL)packet.PopProtocolId();
            long player_id = packet.PopPlayerId();
            byte[] body = packet.PopBody();

            switch (protocol_id)
            {
                case PROTOCOL.U_TO_G_LOGOUT:
                    // TODO DB 붙이기 전까지 일단 안지움
                    // await HandleMessage<U_TO_G_LOGOUT>(cache_helper, player_id, body, Logout);
                    break;
            }
        }

        public async Task Logout(CacheHelper cache_helper, long player_id, U_TO_G_LOGOUT msg)
        {
            var redlock = RedisConnectionPool.GetRedLockFactory(this.redis_connection);

            using (var player_lock = await PlayerController.Lock(redlock, player_id))
            {
                PlayerInfo? player_info = await PlayerController.Load(cache_helper, msg.player_id);

                if (player_info == null)
                {
                    throw new Exception($"can't find player_info. player_id : {player_id}");
                }

                GameObjectInfo object_info = player_info.object_info;
                if (object_info == null)
                {
                    throw new Exception($"can't find object_info. player_id : {player_id}");
                }

                await PlayerController.Delete(cache_helper, player_id);
            }
        }
    }
}
