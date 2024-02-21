using System.Collections.Concurrent;
using System.Net;
using game_server;
using network;

namespace user_server
{
    class Program
    {
        static string server_type = "user_server";

#pragma warning disable CS8618
        public static NetworkService network_service;
#pragma warning restore
        public static ConcurrentQueue<GameUser>? leave_user_queue;
        public static int game_server_num;

        static void Main()
        {
            if (
                !Int32.TryParse(
                    Environment.GetEnvironmentVariable("GAME_SERVER_NUM"),
                    out game_server_num
                )
            )
            {
                LogManager.WriteErrorLog(new Exception("Invalid Game Server Num"));
            }

            LogManager.Initialize(server_type, 0);
            PacketBufferManager.Initialize(Config.MAX_CONNECTION);

            network_service = new();
            leave_user_queue = new();

            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                try
                {
                    GameUser user = new(token);
                }
                catch (Exception e)
                {
                    LogManager.WriteErrorLog(e);
                }
            };

            network_service.Listen(IPAddress.Any, Config.USER_SERVER_PORT);

            Task.Run(ProcessLeaveUser);

            LogManager.WriteInfoLog($"user server start. max_connection: {Config.MAX_CONNECTION}");
        }

        public static async Task ProcessLeaveUser()
        {
            while (true)
            {
                while (leave_user_queue!.TryDequeue(out GameUser? user))
                {
                    if (user == null)
                    {
                        continue;
                    }

                    // LogManager.WriteDebugLog($"client disconnect. player_id: {user.player_id}");
                    await user.ReleaseAsync();
                }
                await Task.Delay(100);
            }
        }
    }
}
