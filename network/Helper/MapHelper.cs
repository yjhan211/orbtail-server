namespace network
{
    // 개발 기간 이슈로 MAP_CITY_1, MAP_FOREST_1는 동일한 맵 크기, 동일한 파티셔닝 구조로 감
    // 추후 달라진다면 MapHelper 분리 필요
    public static class MapHelper
    {
        public static Dictionary<string, int> part_by_position_key = new();
        public static Dictionary<MapID, Dictionary<int, List<string>>> position_list_by_map_part =
            new();

        public static List<Cell> part_pivot_list =
            new()
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

        public static int[][] map_partition = new int[][]
        {
            new int[] { 1, 2 },
            new int[] { 11, 12 },
            new int[] { 3, 4 },
            new int[] { 13, 14 },
            new int[] { 5, 6 },
            new int[] { 15, 16 },
            new int[] { 7, 8 },
            new int[] { 17, 18 },
            new int[] { 9, 10 },
            new int[] { 19, 20 },
            new int[] { 21, 22 },
            new int[] { 31, 32 },
            new int[] { 23, 24 },
            new int[] { 33, 34 },
            new int[] { 25, 26 },
            new int[] { 35, 36 },
            new int[] { 27, 28 },
            new int[] { 37, 38 },
            new int[] { 29, 30 },
            new int[] { 39, 40 }
        };

        public static Dictionary<string, (MapID, Cell, bool)> portal_info =
            new()
            {
                { GetPositionKey(MapID.CITY_1, new(89, 141)), (MapID.FOREST_1, new(59, 66), true) },
                { GetPositionKey(MapID.CITY_1, new(89, 142)), (MapID.FOREST_1, new(59, 66), true) },
                { GetPositionKey(MapID.CITY_1, new(90, 141)), (MapID.FOREST_1, new(59, 66), true) },
                { GetPositionKey(MapID.CITY_1, new(90, 142)), (MapID.FOREST_1, new(59, 66), true) },
                { GetPositionKey(MapID.FOREST_1, new(60, 69)), (MapID.CITY_1, new(89, 139), true) },
                { GetPositionKey(MapID.FOREST_1, new(60, 70)), (MapID.CITY_1, new(89, 139), true) },
                { GetPositionKey(MapID.FOREST_1, new(59, 70)), (MapID.CITY_1, new(89, 139), true) },
                { GetPositionKey(MapID.FOREST_1, new(59, 69)), (MapID.CITY_1, new(89, 139), true) },
            };

        public static void Initialize()
        {
            foreach (var map_id in new List<MapID>() { MapID.CITY_1, MapID.FOREST_1 })
            {
                position_list_by_map_part[map_id] = new();

                var part_number = 1;
                foreach (var part_pivot in part_pivot_list)
                {
                    var pivot_cell = Cell.Clone(part_pivot);
                    var cell_list = GetBoundCellList(pivot_cell);

                    position_list_by_map_part[map_id][part_number] = new();
                    foreach (var cell in cell_list)
                    {
                        var position_key = GetPositionKey(map_id, cell);
                        if (!part_by_position_key.TryGetValue(position_key, out var duplicate))
                        {
                            part_by_position_key.Add(position_key, part_number);
                            position_list_by_map_part[map_id][part_number].Add(position_key);
                        }
                    }
                    part_number++;
                }
            }
        }

        public static string GetMoveManageSubject(MapID map_id, int server_id)
        {
            return $"move_object_{map_id}_{server_id}";
        }

        public static string GetLeaveManageSubject(MapID map_id, int server_id)
        {
            return $"leave_object_{map_id}_{server_id}";
        }

        public static string GetSpawnManageSubject(MapID map_id, int server_id)
        {
            return $"spawn_object_{map_id}_{server_id}";
        }

        public static string GetDestroyObjectSubject(MapID map_id, int server_id)
        {
            return $"destroy_object_{map_id}_{server_id}";
        }

        public static string GetUpdatePlayerSubject(MapID map_id, int server_id)
        {
            return $"update_player_{map_id}_{server_id}";
        }

        public static string GetUpdateJobResourceSubject(MapID map_id, int server_id)
        {
            return $"update_job_resource_{map_id}_{server_id}";
        }

        public static string GetBrodcastMoveSubject(MapID map_id, int server_id)
        {
            return $"broadcast_move_{map_id}_{server_id}";
        }

        public static string GetBrodcastDestroySubject(MapID map_id, int server_id)
        {
            return $"broadcast_destroy_{map_id}_{server_id}";
        }

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

        public static List<int> GetBoundServerList(MapID map_id, int total_server_num, Cell cell)
        {
            return GetBoundCellList(cell)
                .Select((bound_cell) => GetPositionKey(map_id, bound_cell))
                .Select((position_key) => GetServerIdByPositionKey(total_server_num, position_key))
                .Where((server_id) => server_id != 0)
                .Distinct()
                .ToList();
        }

        public static int GetServerIdByPositionKey(int total_server_num, string position_key)
        {
            if (!part_by_position_key.TryGetValue(position_key, out int part_number))
            {
                return 0;
            }

            int server_id = 0;

            switch (total_server_num)
            {
                case 40:
                    server_id = part_number;
                    break;

                case 20:
                    server_id =
                        Array.FindIndex(map_partition, parts => parts.Contains(part_number)) + 1;
                    break;

                case 10:
                    server_id =
                        (
                            Array.FindIndex(map_partition, parts => parts.Contains(part_number))
                            + 1
                            + 1
                        ) / 2;
                    break;

                case 5:
                    server_id =
                        (
                            Array.FindIndex(map_partition, parts => parts.Contains(part_number))
                            + 1
                            + 3
                        ) / 4;
                    break;

                case 2:
                    server_id =
                        Array.FindIndex(map_partition, parts => parts.Contains(part_number)) < 10
                            ? 1
                            : 2;
                    break;

                default:
                    throw new Exception("Invalid total server num");
            }

            return server_id;
        }

        public static string GetPositionKey(MapID map_id, Cell cell)
        {
            return $"map_{map_id}|{cell.x},{cell.y}";
        }

        public static Cell GetCell(string position_key)
        {
            var split = position_key.Split("|");
            var position = split[1].Split(",");

            return new Cell(Int32.Parse(position[0]), Int32.Parse(position[1]));
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

        public static List<Cell> GetBoundCellList(Cell pivot_cell, bool client_view = false)
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

                // 캐릭이 범위보다 아래에 있음. 클라에선 맨 윗줄 날려버림
                if (client_view)
                {
                    for (int y = min_y; y <= max_y; y++)
                    {
                        int dx = System.Math.Abs(x - pivot_cell.x);
                        int dy = System.Math.Abs(y - pivot_cell.y);

                        if (
                            (dx >= 8 && dx <= 14)
                            && (dy >= 8 && dy <= 14)
                            && (dx + dy == 22 || dx + dy == 23)
                        )
                        {
                            continue;
                        }

                        Cell cell = new(x, y);
                        result.Add(cell);
                    }
                    continue;
                }

                for (int y = min_y; y <= max_y; y++)
                {
                    Cell cell = new(x, y);
                    result.Add(cell);
                }
            }

            return result;
        }

        public static Cell GetRandomCell()
        {
            Random random = new();
            return part_pivot_list[random.Next(0, part_pivot_list.Count)];
        }

        public static int GetDistance(Cell cell1, Cell cell2)
        {
            int dx = Math.Abs(cell1.x - cell2.x);
            int dy = Math.Abs(cell1.y - cell2.y);

            return dx + dy;
        }
    }
}
