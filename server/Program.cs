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

            // test();
        }

        static void test()
        {
            var owner = new Cell(15, 15);

            const int X_MIN_BOUND = -11;
            const int X_MAX_BOUND = 14;

            const int Y_MIN_BOUND = -5;
            const int Y_MAX_BOUND = -6;

            var min_x = Math.Max(0, owner.x + X_MIN_BOUND);
            var max_x = Math.Min(100, owner.x + X_MAX_BOUND);

            var min_y = Math.Max(0, owner.y + Y_MIN_BOUND);
            var max_y = Math.Min(100, owner.y + Y_MAX_BOUND);

            var line = 0;
            for (int x = min_x; x <= max_x; x++)
            {
                line += 1;

                if (line <= 6)
                {
                    min_y = Math.Max(0, min_y - 1);
                }
                else if (7 < line)
                {
                    min_y = Math.Min(100, min_y + 1);
                }

                if (line <= 20)
                {
                    max_y = Math.Min(100, max_y + 1);
                }
                else if (21 < line)
                {
                    max_y = Math.Max(0, max_y - 1);
                }

                Console.Write($"[{line}]");

                for (int y = min_y; y <= max_y; y++)
                {
                    Console.Write($"({x},{y})");
                }

                Console.WriteLine("");
            }
        }
    }
}
