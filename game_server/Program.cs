using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static GameServer game_server;
        public static int server_id = 0;
        public static int game_server_num;
        public static string[] redis_endpoints;
        public static string nats_endpoint;
#pragma warning restore

        async static Task Main(string[] args)
        {
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("SERVER_ID"), out server_id))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Server Id"));
            }
            LogManager.Initialize("game_server", server_id);

            string? game_server_env = Environment.GetEnvironmentVariable("GAME_SERVER_NUM");
            if (!Int32.TryParse(game_server_env, out game_server_num))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Game Server Num"));
                return;
            }

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

            game_server = new();
            game_server.Start();

            LogManager.WriteInfoLog($"Server Start. server_id: {server_id}");
            await Task.Delay(-1);
        }
    }
}
