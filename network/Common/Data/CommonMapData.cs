using network.common.data.models;

namespace network.common.data;

// 현재 공통맵은 동일한 맵 크기, 동일한 파티셔닝 구조로 감. 추후 달라진다면 MapHelper 분리 필요
public static class CommonMapData
{
    private static int _totalServerNum;
    private static readonly Dictionary<string, int> PartByPositionKey = new();
    private static readonly Dictionary<MapId, Dictionary<int, List<string>>> PositionListByMapPart = new();

    private static readonly Dictionary<MapId, List<int>> GenExploreIdList = new();

    private static readonly List<Cell> PartPivotList =
    [
        new(25, 85), new(31, 79), new(37, 73), new(43, 67), new(49, 61), new(55, 55), new(61, 49), new(67, 43),
        new(73, 37), new(79, 31),
        new(45, 105), new(51, 99), new(57, 93), new(63, 87), new(69, 81), new(75, 75), new(81, 69), new(87, 63),
        new(93, 57), new(99, 51),
        new(65, 125), new(71, 119), new(77, 113), new(83, 107), new(89, 101), new(95, 95), new(101, 89),
        new(107, 83), new(113, 77), new(119, 71),
        new(85, 145), new(91, 139), new(97, 133), new(103, 127), new(109, 121), new(115, 115), new(121, 109),
        new(127, 103), new(133, 97), new(139, 91)
    ];

    private static readonly int[][] MapPartition =
    [
        [1, 2], [11, 12], [3, 4], [13, 14],
        [5, 6], [15, 16], [7, 8], [17, 18],
        [9, 10], [19, 20], [21, 22], [31, 32],
        [23, 24], [33, 34], [25, 26], [35, 36],
        [27, 28], [37, 38], [29, 30], [39, 40]
    ];

    // ReSharper disable once CollectionNeverUpdated.Local
    private static readonly Dictionary<string, (MapId, Cell, bool)> PortalInfo = new()
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

    public static List<string> GetPositionListByMapByPart(MapId mapId, int part)
    {
        return PositionListByMapPart[mapId][part];
    }

    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
        // TODO 공통맵 생기면 추가
        // ReSharper disable once RedundantEmptyObjectOrCollectionInitializer
        foreach (var mapId in new List<MapId> { })
        {
            PositionListByMapPart[mapId] = new Dictionary<int, List<string>>();

            var partNumber = 1;
            var boundCellLists = PartPivotList.Select(partPivotCell => partPivotCell.GetBoundCellList());
            foreach (var cellList in boundCellLists)
            {
                PositionListByMapPart[mapId][partNumber] = [];

                var uniquePositionKeys = cellList.Select(cell => CreatePartKey(mapId, cell))
                    .Where(positionKey => !PartByPositionKey.TryGetValue(positionKey, out _));

                foreach (var positionKey in uniquePositionKeys)
                {
                    PartByPositionKey.Add(positionKey, partNumber);
                    PositionListByMapPart[mapId][partNumber].Add(positionKey);
                }

                partNumber++;
            }
        }

        // TODO 공통맵 생기면 추가
        // GenExploreIdList.Add(MapId.CAMPUS_1, [100001]);
    }

    public static bool IsCommonMap(MapId mapId)
    {
        return mapId switch
        {
            MapId.LIBRARY => false,
            _ => true
        };
    }

    public static string CreatePartKey(MapId mapId, Cell cell)
    {
        return $"{mapId}|{cell.X},{cell.Y}";
    }

    private static string GetPortalKey(MapId mapId, Cell cell)
    {
        return $"{mapId}|{cell.X},{cell.Y}";
    }

    public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo)
    {
        var portalKey = GetPortalKey(objectInfo.MapId, objectInfo.TargetCell);
        if (PortalInfo.TryGetValue(portalKey, out var portalInfo)) return portalInfo;

        return null;
    }

    public static int GetManageServerId(string positionKey)
    {
        if (!PartByPositionKey.TryGetValue(positionKey, out var partNumber)) return 0;

        var serverId = _totalServerNum switch
        {
            40 => partNumber,
            20 => Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1,
            10 => (Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1 + 1) / 2,
            5 => (Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1 + 3) / 4,
            2 => Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) < 10 ? 1 : 2,
            _ => throw new Exception("Invalid TotalServerNum")
        };

        return serverId;
    }

    public static Cell CreateCell(string partKey)
    {
        var split = partKey.Split("|");
        var position = split[1].Split(",");

        return new Cell(int.Parse(position[0]), int.Parse(position[1]));
    }

    public static List<int>? GetGenExploreIdList(MapId mapId)
    {
        return GenExploreIdList.GetValueOrDefault(mapId);
    }

    public static List<int> GetBoundServerList(MapId mapId, Cell cell)
    {
        return cell.GetBoundCellList().Select(boundCell => CreatePartKey(mapId, boundCell))
            .Select(GetManageServerId)
            .Where(serverId => serverId != 0)
            .Distinct()
            .ToList();
    }

    public static int GetManagePartByPositionKey(string positionKey)
    {
        if (!PartByPositionKey.TryGetValue(positionKey, out var partNumber))
            throw new Exception($"Can't find part_number for position_key: {positionKey}");

        var serverId = GetManageServerId(positionKey);
        var managePart = _totalServerNum switch
        {
            40 => partNumber,
            20 => MapPartition[serverId - 1][0],
            10 => MapPartition[(serverId - 1) * 2][0],
            5 => MapPartition[(serverId - 1) * 4][0],
            2 => serverId == 1 ? MapPartition[0][0] : MapPartition[10][0],
            _ => throw new Exception("Invalid TotalServerNum")
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
                result.AddRange(MapPartition[gameServerId - 1]);
                break;

            case 10:
                for (var i = 2 * gameServerId - 2; i < 2 * gameServerId; i++) result.AddRange(MapPartition[i]);
                break;

            case 5:
                for (var i = 4 * (gameServerId - 1); i < 4 * gameServerId; i++) result.AddRange(MapPartition[i]);
                break;

            case 2:
                var startIndex = gameServerId == 1 ? 0 : 10;
                for (var i = startIndex; i < startIndex + 10; i++) result.AddRange(MapPartition[i]);
                break;

            default:
                throw new Exception("Invalid TotalServerNum");
        }

        return result;
    }

    public static Cell GetRandomCell()
    {
        var random = new Random();
        return PartPivotList[random.Next(0, PartPivotList.Count)];
    }
}