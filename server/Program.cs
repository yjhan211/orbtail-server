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
        public static GameServer game_server = new();

#pragma warning disable CS8618
        public static RedisConnection redis_client;
#pragma warning restore

        static async Task Main(string[] args)
        {
            game_server.Start();
            redis_client = await RedisConnection.InitializeAsync(
                connectionString: Config.REDIS_CONFIG
            );
        }
    }
}
