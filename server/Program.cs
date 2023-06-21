#pragma warning disable IDE0060

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
        readonly static List<GameUser> user_list = new();

        static void Main(string[] args)
        {
            PacketBufferManager.Initialize(2000);

            NetworkService network_service = new();
            network_service.Initialize();
            network_service.session_created_callback += (UserToken token) =>
            {
                GameUser user = new(token);
                lock (user_list)
                {
                    user_list.Add(user);
                }
            };

            network_service.Listen("0.0.0.0", 7979, 100);
            Console.WriteLine("Started!");
        }

        public static void RemoveUser(GameUser user)
        {
            lock (user_list)
            {
                user_list.Remove(user);
            }
        }
    }
}
