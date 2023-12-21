#pragma warning disable IDE0060

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static GameServer game_server;
        public static RedisConnection redis_connection;
#pragma warning restore

        static async Task Main()
        {
            redis_connection = await RedisConnection.InitializeAsync(
                connectionString: Config.REDIS_CONFIG
            );

            CacheHelper.Initialize(redis_connection);
            LockHelper.Initialize(redis_connection);

            game_server = new();
            game_server.Start();
        }
    }
}
