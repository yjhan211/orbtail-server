using network.common;

namespace network.helpers;

public static class MapHelper
{
    private static int _totalServerNum;

    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
    }

    public static string CreatePartKey(MapId mapId, long mapSubId)
    {
        return $"{mapId}|{mapSubId}";
    }

    public static int GetManageServerId(long mapSubId)
    {
        int result = (int)((mapSubId - 1) % _totalServerNum) + 1;
        return result;
    }
}
