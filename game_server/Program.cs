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

        static void Main(string[] args)
        {
            server_id = int.Parse(args[0]);

            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            game_server = new();
            game_server.Start(server_id);

            Console.WriteLine("[GameServer] Game Server Start");
            Console.ReadKey();
        }
    }
}
