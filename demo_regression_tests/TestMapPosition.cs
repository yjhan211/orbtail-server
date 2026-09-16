using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

// 구역을 별도 지정하지 않고 실제 맵 좌표로 테스트 개체를 배치한다.
internal static class TestMapPosition
{
    internal static Vector3f In(AreaType area, float x = 0f, float y = 0f)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        return new Vector3f(position.X + x, position.Y + y, 0f);
    }
}
