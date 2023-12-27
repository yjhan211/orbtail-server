using System.Net;
using network;

namespace user_server
{
    class Program
    {
#pragma warning disable CS8618
        public static NetworkService network_service;
        public static RedisConnection redis_connection;
        public static CacheHelper cache_helper;
        public static LockHelper lock_helper;
#pragma warning restore

        static async Task Main()
        {
            redis_connection = await RedisConnection.InitializeAsync(
                connectionString: Config.REDIS_CONFIG
            );

            cache_helper = new CacheHelper();
            cache_helper.Initialize(redis_connection);

            lock_helper = new LockHelper();
            lock_helper.Initialize(redis_connection);

            network_service = new();

            PacketBufferManager.Initialize(Config.MAX_CONNECTION);
            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                GameUser user = new(token);
            };

            network_service.Listen(IPAddress.Any, Config.USER_SERVER_PORT);

            Console.WriteLine("[UserServer] User Server Start");
        }
    }
}
