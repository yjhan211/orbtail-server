using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class RouteMovementTests
{
    [Fact]
    public void ClosedDoorsBlockBotsButNotMonsters()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var map = Config.SWARM_MATCH_MAP;
        var from = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var to = GameMapData.GetAreaSpawnCell(map, AreaType.S2Library1);
        var steps = MapPathfinder.FindPath(map, AreaType.S2Corridor9, from, AreaType.S2Library1, to);
        Assert.NotNull(steps);
        var route = steps.Select(step => MapCoordinateConverter.CellToWorld(map, step.Cell)).ToList();
        var start = MapCoordinateConverter.CellToWorld(map, from);
        int botIndex = 0;
        int doorChecks = 0;
        MapPathfinder.AdvanceRoute(map, start, route, ref botIndex, 10000f, _ => { doorChecks++; return false; });
        Assert.True(doorChecks > 0);
        Assert.True(botIndex < route.Count);

        int monsterIndex = 0;
        var monsterPosition = MapPathfinder.AdvanceRoute(map, start, route, ref monsterIndex, 10000f);
        Assert.Equal(route.Count, monsterIndex);
        Assert.Equal(route[^1], monsterPosition);
        int openBotIndex = 0;
        var openBotPosition = MapPathfinder.AdvanceRoute(map, start, route, ref openBotIndex, 10000f, _ => true);
        Assert.Equal(monsterIndex, openBotIndex);
        Assert.Equal(monsterPosition, openBotPosition);
    }

    [Fact]
    public void WallsBlockBothAndDoNotConsumeWaypoint()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var map = Config.SWARM_MATCH_MAP;
        var start = MapCoordinateConverter.CellToWorld(map,
            GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9));
        var route = new[] { new Vector3f(start.X + 100f, start.Y + 100f, 0f) };
        Assert.False(MapPathfinder.IsSegmentWalkable(map, start, route[0]));
        int botIndex = 0;
        int monsterIndex = 0;
        Assert.Equal(start, MapPathfinder.AdvanceRoute(map, start, route, ref botIndex, 1000, _ => true));
        Assert.Equal(start, MapPathfinder.AdvanceRoute(map, start, route, ref monsterIndex, 1000));
        Assert.Equal(0, botIndex);
        Assert.Equal(0, monsterIndex);
    }

    [Fact]
    public void StopsBeforeForbiddenWaypointAndDoesNotSkipIt()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var map = Config.SWARM_MATCH_MAP;
        var cell = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var start = MapCoordinateConverter.CellToWorld(map, cell);
        var target = new Vector3f(start.X + 0.01f, start.Y, 0);
        int index = 0;
        var result = MapPathfinder.AdvanceRoute(map, start, new[] { start, target }, ref index, 10f,
            canEnter: point => point.X <= start.X);
        Assert.Equal(1, index);
        Assert.Equal(start, result);
    }
}
