using System.Net;
using network;

namespace game_server
{
    class Program
    {
#pragma warning disable CS8618
        public static GameServer game_server;
#pragma warning restore

        static void Main()
        {
            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            game_server = new();
            game_server.Start();

            Console.WriteLine("[GameServer] Game Server Start");
            Console.ReadKey();
        }
    }
}
