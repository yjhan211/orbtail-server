using System.Net;
using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static GameServer game_server;
        public static int server_id;
        public static int game_server_num;
#pragma warning restore

        async static Task Main(string[] args)
        {
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("SERVER_ID"), out server_id))
            {
                LogManager.WriteErrorLog(new Exception("Invalid Server Id"));
            }

            if (
                !Int32.TryParse(
                    Environment.GetEnvironmentVariable("GAME_SERVER_NUM"),
                    out game_server_num
                )
            )
            {
                LogManager.WriteErrorLog(new Exception("Invalid Game Server Num"));
            }

            LogManager.Initialize("game_server", server_id);
            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            game_server = new();
            await game_server.Start();

            LogManager.WriteInfoLog($"Server Start. server_id: {server_id}");
            await Task.Delay(-1);
        }
    }
}
