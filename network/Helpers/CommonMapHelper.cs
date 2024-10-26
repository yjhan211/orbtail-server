using network.common;

namespace network.helpers
{
    // 현재 공통맵은 동일한 맵 크기, 동일한 파티셔닝 구조로 감. 추후 달라진다면 MapHelper 분리 필요
    public static class CommonMapHelper
    {
        public static int _totalServerNum = 0;
        private static readonly Dictionary<string, int> _partByPositionKey = new();
        private static readonly Dictionary<MapID, Dictionary<int, List<string>>> _positionListByMapPart = new();
        public static List<string> GetPositionListByMapByPart(MapID mapId, int part) => _positionListByMapPart[mapId][part];
        private static readonly Dictionary<MapID, List<int>> _genExploreIdList = new();
        private static readonly List<Cell> _partPivotList = new()
        {
            new(25, 85), new(31, 79), new(37, 73), new(43, 67), new(49, 61), new(55, 55), new(61, 49), new(67, 43), new(73, 37), new(79, 31),
            new(45, 105), new(51, 99), new(57, 93), new(63, 87), new(69, 81), new(75, 75), new(81, 69), new(87, 63), new(93, 57), new(99, 51),
            new(65, 125), new(71, 119), new(77, 113), new(83, 107), new(89, 101), new(95, 95), new(101, 89), new(107, 83), new(113, 77), new(119, 71),
            new(85, 145), new(91, 139), new(97, 133), new(103, 127), new(109, 121), new(115, 115), new(121, 109), new(127, 103), new(133, 97), new(139, 91)
        };

        private static readonly int[][] _mapPartition =
        {
            new[] { 1, 2 }, new[] { 11, 12 }, new[] { 3, 4 }, new[] { 13, 14 },
            new[] { 5, 6 }, new[] { 15, 16 }, new[] { 7, 8 }, new[] { 17, 18 },
            new[] { 9, 10 }, new[] { 19, 20 }, new[] { 21, 22 }, new[] { 31, 32 },
            new[] { 23, 24 }, new[] { 33, 34 }, new[] { 25, 26 }, new[] { 35, 36 },
            new[] { 27, 28 }, new[] { 37, 38 }, new[] { 29, 30 }, new[] { 39, 40 }
        };

        private static readonly Dictionary<string, (MapID, Cell, bool)> _portalInfo = new()
        {
            // { GetPortalKey(MapID.CAMPUS_1, new(117, 110)), (MapID.FACTORY_1, new(39, 85), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(118, 110)), (MapID.FACTORY_1, new(39, 85), true) },
            // { GetPortalKey(MapID.FACTORY_1, new(39, 90)), (MapID.CAMPUS_1, new(114, 108), true) },
            // { GetPortalKey(MapID.FACTORY_1, new(40, 90)), (MapID.CAMPUS_1, new(114, 108), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(76, 29)), (MapID.WETLAND_1, new(113, 91), false) },
            // { GetPortalKey(MapID.CAMPUS_1, new(76, 30)), (MapID.WETLAND_1, new(113, 91), false) },
            // { GetPortalKey(MapID.WETLAND_1, new(122, 94)), (MapID.CAMPUS_1, new(69, 29), true) },
            // { GetPortalKey(MapID.WETLAND_1, new(122, 95)), (MapID.CAMPUS_1, new(69, 29), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(114, 92)), (MapID.LAB_1, new(90, 96), true) },
        };

        public static void Initialize(int totalServerNum)
        {
            _totalServerNum = totalServerNum;
            foreach (var mapId in new List<MapID>() { MapID.CAMPUS_1 })
            {
                _positionListByMapPart[mapId] = new();

                var partNumber = 1;
                foreach (var partPivotCell in _partPivotList)
                {
                    var cellList = partPivotCell.GetBoundCellList();
                    _positionListByMapPart[mapId][partNumber] = new();
                    foreach (var cell in cellList)
                    {
                        var positionKey = CreatePartKey(mapId, cell);
                        if (!_partByPositionKey.TryGetValue(positionKey, out var duplicate))
                        {
                            _partByPositionKey.Add(positionKey, partNumber);
                            _positionListByMapPart[mapId][partNumber].Add(positionKey);
                        }
                    }
                    partNumber++;
                }
            }

            _genExploreIdList.Add(MapID.CAMPUS_1, new() { 100001 });
        }

        public static bool IsCommonMap(MapID mapId)
        {
            return mapId switch
            {
                MapID.LAB_1 or MapID.LIBRARY => false,
                _ => true,
            };
        }

        public static string CreatePartKey(MapID mapId, Cell cell) => $"{mapId}|{cell.X},{cell.Y}";
        private static string GetPortalKey(MapID mapId, Cell cell) => $"{mapId}|{cell.X},{cell.Y}";
        public static (MapID mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo)
        {
            string portalKey = GetPortalKey(objectInfo.MapId, objectInfo.TargetCell);
            if (_portalInfo.TryGetValue(portalKey, out var PortalInfo))
            {
                return PortalInfo;
            }

            return null;
        }

        public static int GetManageServerId(string positionKey)
        {
            if (!_partByPositionKey.TryGetValue(positionKey, out int part_number))
            {
                return 0;
            }

            int serverId = _totalServerNum switch
            {
                40 => part_number,
                20 => Array.FindIndex(_mapPartition, parts => parts.Contains(part_number)) + 1,
                10 => (Array.FindIndex(_mapPartition, parts => parts.Contains(part_number)) + 1 + 1) / 2,
                5 => (Array.FindIndex(_mapPartition, parts => parts.Contains(part_number)) + 1 + 3) / 4,
                2 => Array.FindIndex(_mapPartition, parts => parts.Contains(part_number)) < 10 ? 1 : 2,
                _ => throw new Exception("Invalid TotalServerNum"),
            };

            return serverId;
        }

        public static Cell CreateCell(string partKey)
        {
            var split = partKey.Split("|");
            var position = split[1].Split(",");

            return new Cell(int.Parse(position[0]), int.Parse(position[1]));
        }

        public static List<int>? GetGenExlporeIdList(MapID mapId) => _genExploreIdList.TryGetValue(mapId, out var result) ? result : null;
        public static List<int> GetBoundServerList(MapID mapId, Cell cell)
        {
            return cell.GetBoundCellList().Select((bound_cell) => CreatePartKey(mapId, bound_cell))
                .Select(GetManageServerId)
                .Where((server_id) => server_id != 0)
                .Distinct()
                .ToList();
        }

        public static int GetManagePartByPositionKey(string positionKey)
        {
            if (!_partByPositionKey.TryGetValue(positionKey, out int partNumber))
            {
                throw new Exception($"Can't find part_number for position_key: {positionKey}");
            }

            int serverId = GetManageServerId(positionKey);
            int managePart = _totalServerNum switch
            {
                40 => partNumber,
                20 => _mapPartition[serverId - 1][0],
                10 => _mapPartition[(serverId - 1) * 2][0],
                5 => _mapPartition[(serverId - 1) * 4][0],
                2 => serverId == 1 ? _mapPartition[0][0] : _mapPartition[10][0],
                _ => throw new Exception("Invalid TotalServerNum"),
            };

            return managePart;
        }

        public static List<int> GetManagePartList(int gameServerId)
        {
            var result = new List<int>();

            switch (_totalServerNum)
            {
                case 40:
                    result.Add(gameServerId);
                    break;

                case 20:
                    result.AddRange(_mapPartition[gameServerId - 1]);
                    break;

                case 10:
                    for (int i = 2 * gameServerId - 2; i < 2 * gameServerId; i++)
                    {
                        result.AddRange(_mapPartition[i]);
                    }
                    break;

                case 5:
                    for (int i = 4 * (gameServerId - 1); i < 4 * gameServerId; i++)
                    {
                        result.AddRange(_mapPartition[i]);
                    }
                    break;

                case 2:
                    var start_index = gameServerId == 1 ? 0 : 10;
                    for (int i = start_index; i < start_index + 10; i++)
                    {
                        result.AddRange(_mapPartition[i]);
                    }
                    break;

                default:
                    throw new Exception("Invalid TotalServerNum");
            }

            return result;
        }

        public static Cell GetRandomCell()
        {
            var random = new Random();
            return _partPivotList[random.Next(0, _partPivotList.Count)];
        }
    }
}
