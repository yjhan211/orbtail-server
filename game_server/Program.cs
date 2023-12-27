using System.Net;
using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static RedisConnection redis_connection;
        public static CacheHelper cache_helper;
        public static LockHelper lock_helper;
        public static GameServer game_server;
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

            game_server = new();
            game_server.Start();

            Console.WriteLine("[GameServer] Game Server Start");

            Console.ReadKey();
        }
    }
}
