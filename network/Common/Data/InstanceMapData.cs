using network.common.models;

namespace network.common.data;

public static class InstanceMapData
{
    private static int _totalServerNum;

    // ReSharper disable once CollectionNeverUpdated.Local
    private static readonly Dictionary<string, (MapId, Cell, bool)> PortalInfo = new()
    {
        // { GetPortalKey(MapID.LAB_1, new(91, 100)), (MapID.CAMPUS_1, new(110, 91), false) }
    };

    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
    }

    public static string CreatePartKey(MapId mapId, long mapSubId)
    {
        return $"map_{mapId}|{mapSubId}";
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

    public static int GetManageServerId(long mapSubId)
    {
        return (int)((mapSubId - 1) % _totalServerNum) + 1;
    }
}