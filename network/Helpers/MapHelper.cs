using network.common;
using network.common.data;
using network.common.data.models;

namespace network.helpers;

public static class MapHelper
{
    private static int _totalServerNum;
    private static readonly Dictionary<string, int> PartByKey = new();
    private static readonly Dictionary<MapId, Dictionary<int, List<string>>> PositionListByMapPart = new();
    
    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
        InitializeCommonMapData();
    }

    private static void InitializeCommonMapData()
    {
        foreach (var mapId in GameMapData.GetCommonMapList())
        {
            var groundRegions = GameMapData.GetMapRegions(mapId)
                .Where(r => r.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!groundRegions.Any()) continue;

            var minX = groundRegions.Min(r => r.Start.X);
            var maxX = groundRegions.Max(r => r.End.X);
            var minY = groundRegions.Min(r => r.Start.Y);
            var maxY = groundRegions.Max(r => r.End.Y);
            
            PositionListByMapPart[mapId] = new();
            var mapWidth = maxX - minX + 1;
            var partWidth = mapWidth / _totalServerNum;

            for (var part = 1; part <= _totalServerNum; part++)
            {
                var startX = minX + (part - 1) * partWidth;
                var endX = part == _totalServerNum ? maxX : startX + partWidth - 1;
            
                var cells = new List<string>();
                for (var x = startX; x <= endX; x++)
                {
                    for (var y = minY; y <= maxY; y++)
                    {
                        if (!IsInGroundRegions(new Cell(x, y), groundRegions)) continue;
                        var key = CreatePartKey(mapId, new Cell(x, y));
                        cells.Add(key);
                        PartByKey[key] = part;
                    }
                }
            
                PositionListByMapPart[mapId][part] = cells;
                Console.WriteLine($"Part {part}: {cells.Count} cells, X({startX}~{endX})");
            }
        }

        Console.WriteLine("\n[End] Common map partitioning");
    }

    private static bool IsInGroundRegions(Cell cell, List<MapRegion> groundRegions)
    {
        return groundRegions.Any(region => 
            cell.X >= region.Start.X && cell.X <= region.End.X &&
            cell.Y >= region.Start.Y && cell.Y <= region.End.Y);
    }

    public static string CreatePartKey(MapId mapId, Cell cell)
    {
        return $"{mapId}|{cell.X},{cell.Y}";
    }

    public static string CreatePartKey(MapId mapId, long mapSubId)
    {
        return $"{mapId}|{mapSubId}";
    }

    public static Cell CreateCell(string partKey)
    {
        var split = partKey.Split("|");
        var position = split[1].Split(",");
        return new Cell(int.Parse(position[0]), int.Parse(position[1]));
    }

    public static List<int> GetBoundServerList(MapId mapId, Cell cell)
    {
        return cell.GetBoundCellList()
            .Select(boundCell => CreatePartKey(mapId, boundCell))
            .Select(GetManageServerId)
            .Where(serverId => serverId != 0)
            .Distinct()
            .ToList();
    }

    public static int GetManageServerId(string partKey)
    {
        var result = PartByKey.GetValueOrDefault(partKey, 0);
        return result;
    }

    public static int GetManageServerId(long mapSubId)
    {
        var result = (int)((mapSubId - 1) % _totalServerNum) + 1;
        return result;
    }

    public static Dictionary<MapId, List<string>> GetManagePartList(int serverId)
    {
        var result = new Dictionary<MapId, List<string>>();
    
        foreach (var mapEntry in PositionListByMapPart)
        {
            var mapId = mapEntry.Key;
            if (mapEntry.Value.TryGetValue(serverId, out var positions))
            {
                result[mapId] = positions;
            }
        }

        return result;
    }
}