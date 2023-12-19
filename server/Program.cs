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
#pragma warning restore

        static async Task Main()
        {
            game_server = new();

            var redis_client = await RedisConnection.InitializeAsync(
                connectionString: Config.REDIS_CONFIG
            );

            CacheHelper.Initialize(redis_client);
            LockHelper.Initialize(redis_client);
            PubSubHelper.Initialize(redis_client);

            game_server.Start();
        }
    }
}
