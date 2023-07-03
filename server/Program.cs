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
        public static GameServer game_server = new();

        static void Main(string[] args)
        {
            game_server.Start();
        }
    }
}
