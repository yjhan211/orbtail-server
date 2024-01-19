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
            LogManager.Initialize("game_server");
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("SERVER_ID"), out server_id))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Server Id"));
            }

            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            game_server = new();
            game_server.Start(server_id);

            LogManager.WriteInfoLog($"Server Start. server_id: {server_id}");
            await Task.Delay(-1);
        }
    }
}
