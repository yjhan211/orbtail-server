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
    }

    private static bool IsInGroundRegions(Cell cell, List<GameMapData.MapRegion> groundRegions)
    {
        return groundRegions.Any(region => 
            cell.X >= region.Start.X && cell.X <= region.End.X &&
            cell.Y >= region.Start.Y && cell.Y <= region.End.Y);
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
