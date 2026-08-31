using network.common;

namespace network.helpers;

public static class MapHelper
{
    private static int _totalServerNum;

    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
    }
}
