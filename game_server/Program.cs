using System.Net;
using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static GameServer game_server;
        public static int server_id;
#pragma warning restore

        async static Task Main(string[] args)
        {
            server_id = int.Parse(args[0]);

            LogManager.Initialize("game_server");
            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            game_server = new();
            game_server.Start(server_id);

            LogManager.WriteInfoLog($"Server Start. server_id: {server_id}");
            await Task.Delay(-1);
        }
    }
}
