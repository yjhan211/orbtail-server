using System.Collections.Concurrent;
using System.Net;
using network;

namespace user_server
{
    class Program
    {
        static string server_type = "user_server";

#pragma warning disable CS8618
#pragma warning disable CS0649
        public static NetworkService network_service;
        public static ConcurrentQueue<GameUser>? leave_user_queue;
        public static int game_server_num;
        public static string[] redis_endpoints;
        public static string nats_endpoint;
#pragma warning restore
        public static CancellationTokenSource cts;

        static void Main()
        {
            string? game_server_env = Environment.GetEnvironmentVariable("GAME_SERVER_NUM");
            if (!Int32.TryParse(game_server_env, out game_server_num))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Game Server Num"));
                return;
            }
            LogManager.Initialize(server_type, 0);

            string? redis_endpoints_env = Environment.GetEnvironmentVariable("REDIS_ENDPOINTS");
            if (string.IsNullOrEmpty(redis_endpoints_env))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Redis EndPoint"));
                return;
            }

            RedisConnectionPool.Initialize(redis_endpoints_env);

            string? nats_endpoint_env = Environment.GetEnvironmentVariable("NATS_ENDPOINT");
            if (string.IsNullOrEmpty(nats_endpoint_env))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Nats EndPoint"));
                return;
            }

            nats_endpoint = nats_endpoint_env;

            PacketBufferManager.Initialize(Config.MAX_CONNECTION);
            MapHelper.Initialize();
            ChatController.Initialize();

            network_service = new();
            leave_user_queue = new();

            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                try
                {
                    GameUser user = new(token);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            };

            network_service.Listen(IPAddress.Any, Config.USER_SERVER_PORT);

            cts = new();
            var leave_user_thread = Task.Run(LeaveUser, cts.Token);

            LogManager.WriteInfoLog($"user server start. max_connection: {Config.MAX_CONNECTION}");
        }

        static ManualResetEventSlim leave_event = new();
        static Timer? leave_user_timer;

        public static void LeaveUser()
        {
            leave_user_timer = new System.Threading.Timer(
                ProcessLeaveUser,
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10)
            );

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    leave_event.Wait(cts.Token);
                    leave_event.Reset();
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

            leave_user_timer.Dispose();
        }

        static void ProcessLeaveUser(object? state)
        {
            try
            {
                if (leave_user_queue!.TryDequeue(out GameUser? user))
                {
                    LogManager.WriteInfoLog($"client disconnect. player_id: {user.player_id}");
                    user.Release();
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                leave_event.Set();
            }
        }
    }
}
