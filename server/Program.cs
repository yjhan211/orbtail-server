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
        readonly static List<GameUser> user_list = new();
        public readonly static GameServer game_server = new GameServer();

        static void Main(string[] args)
        {
            PacketBufferManager.Initialize(2000);

            NetworkService network_service = new();
            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                lock (user_list)
                {
                    GameUser user = new(token);
                    user_list.Add(user);
                }
            };

            network_service.Listen();

            // TODO 파일로깅
            Console.WriteLine("Server Start");
        }

        public static void RemoveUser(GameUser user)
        {
            lock (user_list)
            {
                user_list.Remove(user);
            }
        }

        public static List<GameUser> GetUserList()
        {
            lock (user_list)
            {
                return user_list;
            }
        }
    }
}
