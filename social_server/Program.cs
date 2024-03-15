using network;

namespace social_server
{
    class Program
    {
#pragma warning disable CS8618

        static string server_type = "community_server";
        public static string[] redis_endpoints;
        public static string nats_endpoint;
        static SocialServer social_server;

#pragma warning restore

        async static Task Main()
        {
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

            LogManager.WriteInfoLog(
                $"community server start. max_connection: {Config.MAX_CONNECTION}"
            );

            social_server = new();

            await Task.Delay(-1);
        }
    }
}
