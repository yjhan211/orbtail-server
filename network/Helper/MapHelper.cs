using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace network
{
    public static class MapHelper
    {
        public static Dictionary<string, int> part_by_position_key = new();
        public static Dictionary<int, List<string>> position_list_by_part = new();
        public static List<Cell> part_pivot_list = new()
        {
            new(25, 85),
            new(31, 79),
            new(37, 73),
            new(43, 67),
            new(49, 61),
            new(55, 55),
            new(61, 49),
            new(67, 43),
            new(73, 37),
            new(79, 31),
            new(45, 105),
            new(51, 99),
            new(57, 93),
            new(63, 87),
            new(69, 81),
            new(75, 75),
            new(81, 69),
            new(87, 63),
            new(93, 57),
            new(99, 51),
            new(65, 125),
            new(71, 119),
            new(77, 113),
            new(83, 107),
            new(89, 101),
            new(95, 95),
            new(101, 89),
            new(107, 83),
            new(113, 77),
            new(119, 71),
            new(85, 145),
            new(91, 139),
            new(97, 133),
            new(103, 127),
            new(109, 121),
            new(115, 115),
            new(121, 109),
            new(127, 103),
            new(133, 97),
            new(139, 91)
        };

        public static void InitializeGameServer()
        {
            var part_number = 1;
            foreach (var part_pivot in part_pivot_list)
            {
                var pivot_cell = Cell.Clone(part_pivot);
                var cell_list = GetBoundCellList(pivot_cell);
                position_list_by_part[part_number] = new();

                foreach (var cell in cell_list)
                {
                    var position_key = GetPositionKey(1, cell);
                    if (!part_by_position_key.TryGetValue(position_key, out var duplicate))
                    {
                        // part_by_position_key.Add(position_key, part_number);
                        position_list_by_part[part_number].Add(position_key);
                    }
                }

                part_number++;
            }
        }

        public static void InitializeUserServer()
        {
            var part_number = 1;
            foreach (var part_pivot in part_pivot_list)
            {
                var pivot_cell = Cell.Clone(part_pivot);
                var cell_list = GetBoundCellList(pivot_cell);
                position_list_by_part[part_number] = new();

                foreach (var cell in cell_list)
                {
                    var position_key = GetPositionKey(1, cell);
                    if (!part_by_position_key.TryGetValue(position_key, out var duplicate))
                    {
                        part_by_position_key.Add(position_key, part_number);
                        // position_list_by_part[part_number].Add(position_key);
                    }
                }

                part_number++;
            }
        }

        public static int[][] map_partition = [
            [1,2],[11,12],
            [3,4],[13,14],
            [5,6],[15,16],
            [7,8],[17,18],
            [9,10],[19,20],
            [21,22],[31,32],
            [23,24],[33,34],
            [25,26],[35,36],
            [27,28],[37,38],
            [29,30],[39,40]
        ];

        public static List<int> GetManagePartList(int total_server, int game_server_id)
        {
            var result = new List<int>();

            switch (total_server)
            {
                case 40:
                    result.Add(game_server_id);
                    break;

                case 20:
                    result.AddRange(map_partition[game_server_id - 1]);
                    break;

                case 10:
                    for (int i = 2 * game_server_id - 2; i < 2 * game_server_id; i++)
                    {
                        result.AddRange(map_partition[i]);
                    }
                    break;

                case 5:
                    for (int i = 4 * (game_server_id - 1); i < 4 * game_server_id; i++)
                    {
                        result.AddRange(map_partition[i]);
                    }
                    break;

                case 2:
                    var start_index = game_server_id == 1 ? 0 : 10;
                    for (int i = start_index; i < start_index + 10; i++)
                    {
                        result.AddRange(map_partition[i]);
                    }
                    break;

                default:
                    throw new Exception("invalid Total Server num");
            }

            return result;
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

        public static string GetMapKey()
        {
            return $"map_{1}";
        }

        public static string GetMapKey(int map_id)
        {
            return $"map_{map_id}";
        }

        public static string GetPositionKey(Cell cell)
        {
            return $"{GetMapKey()}|{cell.x},{cell.y}";
        }

        public static Cell CalcTargetCell(Cell cell, DirectionType direction)
        {
            Cell clone = Cell.Clone(cell);

            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                    clone.x += 1;
                    break;

                case DirectionType.TOP_RIGHT:
                    clone.x -= 1;
                    break;

                case DirectionType.BOTTOM_LEFT:
                    clone.y -= 1;
                    break;

                case DirectionType.BOTTOM_RIGHT:
                    clone.y += 1;
                    break;
            }

            return clone;
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
                    result.Add(cell);
                }
            }

            return result;
        }

        public static (int, int) DetermineDivisions(int total_server_num)
        {
            int sqrt = (int)Math.Sqrt(total_server_num);
            int horizontal_divisions = sqrt;
            int vertical_divisions = sqrt;

            while (horizontal_divisions * vertical_divisions < total_server_num)
            {
                if (horizontal_divisions <= vertical_divisions)
                    horizontal_divisions++;
                else
                    vertical_divisions++;
            }

            return (horizontal_divisions, vertical_divisions);
        }

        public static Cell GetRandomCell()
        {
            var target_list = new List<Cell>()
            {
                new(25, 85),
                new(31, 79),
                new(37, 73),
                new(43, 67),
                new(49, 61),
                new(55, 55),
                new(61, 49),
                new(67, 43),
                new(73, 37),
                new(79, 31),
                new(45, 105),
                new(51, 99),
                new(57, 93),
                new(63, 87),
                new(69, 81),
                new(75, 75),
                new(81, 69),
                new(87, 63),
                new(93, 57),
                new(99, 51),
                new(65, 125),
                new(71, 119),
                new(77, 113),
                new(83, 107),
                new(89, 101),
                new(95, 95),
                new(101, 89),
                new(107, 83),
                new(113, 77),
                new(119, 71),
                new(85, 145),
                new(91, 139),
                new(97, 133),
                new(103, 127),
                new(109, 121),
                new(115, 115),
                new(121, 109),
                new(127, 103),
                new(133, 97),
                new(139, 91)
            };

            Random random = new();
            return target_list[random.Next(0, target_list.Count)];
        }
    }
}
