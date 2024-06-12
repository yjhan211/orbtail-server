namespace game_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public class GameServer
    {
        CancellationTokenSource cts;

        // Task? logic_thread;
        List<MapController> map_controller_list;
        InstanceController instance_controller;
        ConnectionMultiplexer redis_connection;
        CacheHelper cache_helper;

        public GameServer()
        {
            this.cts = new();
            this.map_controller_list = new();

            this.map_controller_list.Add(new(MapID.CAMPUS_1));
            this.map_controller_list.Add(new(MapID.FACTORY_1));
            this.instance_controller = new();

            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new(this.redis_connection);
        }

        public async Task Start()
        {
            foreach (var map_controller in this.map_controller_list)
            {
                await map_controller.Initialize(new(Program.nats_endpoint));
            }

            this.instance_controller.Initialize(new(Program.nats_endpoint));
            var logic_thread = Task.Run(GameLoop, this.cts.Token);
        }

        ManualResetEventSlim message_event = new();
        Timer? message_timer;

        void GameLoop()
        {
            this.message_timer = new Timer(
                ProcessMessages,
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10)
            );

            while (!this.cts.Token.IsCancellationRequested)
            {
                try
                {
                    this.message_event.Wait(this.cts.Token);
                    this.message_event.Reset();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            }

            this.message_timer.Dispose();
        }

        async void ProcessMessages(object? state)
        {
            try
            {
                byte[]? message = cache_helper.Dequeue("game_server_queue");
                if (message != null)
                {
                    Packet? packet = new(message);
                    if (packet != null)
                    {
                        await ProcessReceiveAsync(cache_helper, packet).ConfigureAwait(false);
                        Packet.Destroy(packet);
                    }
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                this.message_event.Set();
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
                    await HandleMessage<U_TO_G_LOGOUT>(cache_helper, player_id, body, Logout);
                    break;
            }
        }

        public async Task Logout(CacheHelper cache_helper, long player_id, U_TO_G_LOGOUT msg)
        {
            var redlock = RedisConnectionPool.GetRedLockFactory(this.redis_connection);

            using (var player_lock = await PlayerInfoController.Lock(redlock, player_id))
            {
                PlayerInfo? player_info = PlayerInfoController.Load(cache_helper, msg.player_id);

                if (player_info == null)
                {
                    throw new Exception($"can't find player_info. player_id : {player_id}");
                }

                GameObjectInfo object_info = player_info.object_info;
                if (object_info == null)
                {
                    throw new Exception($"can't find object_info. player_id : {player_id}");
                }

                // TODO DB 붙이기 전까지 일단 안지움
                // await PlayerInfoController.Delete(cache_helper, player_id);
            }
        }
    }
}
