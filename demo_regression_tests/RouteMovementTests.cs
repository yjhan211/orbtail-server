using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class RouteMovementTests
{
    private static MatchRuntime CreateRuntime()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        return TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987621);
    }

    private static MovementState CreatePath(IEnumerable<Vector3f> points)
    {
        var state = new MovementState();
        state.Waypoints.AddRange(points);
        return state;
    }

    [Fact]
    public void ClosedDoorsBlockBotsButNotMonsters()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        runtime.Doors.CloseDoorsForAreas(GameDoorData.GetAll().Select(door => door.AreaType));
        var map = Config.SWARM_MATCH_MAP;
        var from = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var to = GameMapData.GetAreaSpawnCell(map, AreaType.S2Library1);
        var steps = MapPathfinder.FindPath(map, AreaType.S2Corridor9, from, AreaType.S2Library1, to);
        Assert.NotNull(steps);
        var route = steps.Select(step => MapCoordinateConverter.CellToWorld(map, step.Cell)).ToList();
        var start = MapCoordinateConverter.CellToWorld(map, from);
        var bot = CreatePath(route);
        var monster = CreatePath(route);
        MatchMovementService.AdvanceRoute(runtime, bot, start, 10000f);
        Assert.True(bot.WaypointIndex < route.Count);
        var monsterPosition = MatchMovementService.AdvanceRoute(runtime, monster, start, 10000f, ignoreClosedDoors: true);
        Assert.Equal(route.Count, monster.WaypointIndex);
        Assert.Equal(route[^1], monsterPosition);
        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        var openBot = CreatePath(route);
        var openBotPosition = MatchMovementService.AdvanceRoute(runtime, openBot, start, 10000f);
        Assert.Equal(monster.WaypointIndex, openBot.WaypointIndex);
        Assert.Equal(monsterPosition, openBotPosition);
    }

    [Fact]
    public void WallsBlockBothAndDoNotConsumeWaypoint()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var map = Config.SWARM_MATCH_MAP;
        var start = MapCoordinateConverter.CellToWorld(map,
            GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9));
        var target = new Vector3f(start.X + 100f, start.Y + 100f, 0f);
        Assert.False(MapPathfinder.IsSegmentWalkable(map, start, target));
        var bot = CreatePath([target]);
        var monster = CreatePath([target]);
        Assert.Equal(start, MatchMovementService.AdvanceRoute(runtime, bot, start, 1000));
        Assert.Equal(start, MatchMovementService.AdvanceRoute(runtime, monster, start, 1000, ignoreClosedDoors: true));
        Assert.Equal(0, bot.WaypointIndex);
        Assert.Equal(0, monster.WaypointIndex);
    }

    [Fact]
    public void StopsBeforeForbiddenWaypointAndDoesNotSkipIt()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var map = Config.SWARM_MATCH_MAP;
        var cell = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var start = MapCoordinateConverter.CellToWorld(map, cell);
        var target = new Vector3f(start.X + 0.01f, start.Y, 0);
        var path = CreatePath([start, target]);
        var result = MatchMovementService.AdvanceRoute(runtime, path, start, 10f,
            canEnter: point => point.X <= start.X);
        Assert.Equal(1, path.WaypointIndex);
        Assert.Equal(start, result);
    }

    [Fact]
    public void RequiresMatchLockAndKeepsPerEntityProgressIndependent()
    {
        var runtime = CreateRuntime();
        var start = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var first = CreatePath([start]);
        var second = CreatePath([start]);
        Assert.Throws<InvalidOperationException>(() => MatchMovementService.AdvanceRoute(runtime, first, start, 1));
        using var scope = runtime.Enter();
        MatchMovementService.AdvanceRoute(runtime, first, start, 1);
        Assert.Equal(1, first.WaypointIndex);
        Assert.Equal(0, second.WaypointIndex);
        first.Clear();
        Assert.Single(second.Waypoints);
    }
}
