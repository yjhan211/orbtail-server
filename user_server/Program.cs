using System.Net;
using network;

namespace user_server
{
    class Program
    {
#pragma warning disable CS8618
        public static NetworkService network_service;
#pragma warning restore

        static async Task Main()
        {
            network_service = new();

            PacketBufferManager.Initialize(Config.MAX_CONNECTION);
            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                GameUser user = new(token);
                // await user.initializeAsync();
            };

            network_service.Listen(IPAddress.Any, Config.USER_SERVER_PORT);

            Console.WriteLine("[UserServer] User Server Start");
        }
    }
}
