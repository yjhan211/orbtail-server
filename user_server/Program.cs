using System.Collections.Concurrent;
using System.Net;
using game_server;
using network;

namespace user_server
{
    class Program
    {
        static string server_type = "user_server";

#pragma warning disable CS8618
        public static NetworkService network_service;
        public static ConcurrentQueue<GameUser>? leave_user_queue;
        public static int game_server_num;
        public static string[] redis_endpoints;
        public static string nats_endpoint;
#pragma warning restore

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

            MapHelper.InitializeUserServer();

            network_service.Listen(IPAddress.Any, Config.USER_SERVER_PORT);

            Task.Run(ProcessLeaveUser);

            LogManager.WriteInfoLog($"user server start. max_connection: {Config.MAX_CONNECTION}");
        }

        public static async Task ProcessLeaveUser()
        {
            while (true)
            {
                while (leave_user_queue!.TryDequeue(out GameUser? user))
                {
                    if (user == null)
                    {
                        continue;
                    }

                    LogManager.WriteInfoLog($"client disconnect. player_id: {user.player_id}");
                    user.Release();
                }
                await Task.Delay(10);
            }
        }
    }
}
