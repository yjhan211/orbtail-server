#pragma warning disable IDE0060 // 사용하지 않는 매개 변수를 제거하세요.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using network;

namespace server
{
    class Program
    {
        static List<GameUser> user_list = new();

        static void Main(string[] args)
        {
            PacketBufferManager.initialize(2000);

            NetworkService network_service = new NetworkService();
            network_service.session_created_callback += onSessionCreated;
            network_service.initialize();
            network_service.listen("0.0.0.0", 7979, 100);

            Console.WriteLine("Started!");
            Console.ReadLine();
        }

        static void onSessionCreated(UserToken token)
        {
            GameUser user = new GameUser(token);
            lock (user_list)
            {
                user_list.Add(user);
            }
        }

        public static void removeUser(GameUser user)
        {
            lock (user_list)
            {
                user_list.Remove(user);
            }
        }
    }
}
