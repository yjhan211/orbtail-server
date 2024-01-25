using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace network
{
    public static class MapHelper
    {
        public const int MAP_SIZE = 100;

        public static bool IsOutOfMapRange(Cell cell)
        {
            return cell.x < 0 || cell.x >= MAP_SIZE || cell.y < 0 || cell.y >= MAP_SIZE;
        }

        public static Cell GetRandomCell()
        {
            Random random = new();
            return new(random.Next(0, MAP_SIZE), random.Next(0, MAP_SIZE));
        }

        public static string GetPositionKey(int map_id, Cell cell)
        {
            return $"map_{map_id}|{cell.x},{cell.y}";
        }

        public static Cell GetCell(string position_key)
        {
            var split = position_key.Split("|");
            var position = split[1].Split(",");

            return new Cell(Int32.Parse(position[0]), Int32.Parse(position[1]));
        }

        public static List<Cell> GetBoundCellList(Cell pivot_cell)
        {
            List<Cell> result = new();

            int min_x = pivot_cell.x - 13;
            int max_x = pivot_cell.x + 14;

            int min_y = pivot_cell.y - 7;
            int max_y = pivot_cell.y - 9;

            int line = 0;

            for (int x = min_x; x <= max_x; x++)
            {
                line += 1;

                if (line <= 1)
                {
                    min_y -= 1;
                    max_y += 2;
                }
                else if (line <= 4)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 5)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 6)
                {
                    min_y -= 1;
                    max_y += 1;
                }
                else if (line == 7)
                {
                    max_y += 1;
                }
                else if (line == 8)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line == 9)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line <= 22)
                {
                    min_y += 1;
                    max_y += 1;
                }
                else if (line == 23)
                {
                    min_y += 1;
                }
                else
                {
                    min_y += 1;
                    max_y -= 1;
                }

                for (int y = min_y; y <= max_y; y++)
                {
                    Cell cell = new(x, y);

                    if (IsOutOfMapRange(cell))
                    {
                        continue;
                    }

                    result.Add(cell);
                }
            }

            return result;
        }
    }
}
