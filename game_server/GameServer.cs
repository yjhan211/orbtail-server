namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;

    public class GameServer
    {
        NatsClient nats_client;
        CancellationTokenSource cts;
        Task? logic_thread;
        MapController map_controller;

        public GameServer()
        {
            this.nats_client = new(Program.nats_endpoint);
            this.cts = new();
            this.map_controller = new();
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
                    var redis_connection = await RedisConnection.InitializeAsync();
                    CacheHelper cache_helper = new(redis_connection);
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
                            await ProcessReceiveAsync(packet);
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
                    case PROTOCOL.U_TO_G_LOGOUT:
                        await HandleMessage<U_TO_G_LOGOUT>(redis_conn, player_id, body, Logout);
                        break;
                }
            }
        }

        public async Task Logout(RedisConnection redis_conn, long player_id, U_TO_G_LOGOUT msg)
        {
            CacheHelper cache_helper = new(redis_conn);
            var redlock = redis_conn.GetRedLockFactory();

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
