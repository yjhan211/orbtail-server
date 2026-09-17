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
        state.Waypoints.AddRange(points.Select(point => MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, point)));
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
        var info = new GameObjectInfo {  MapId = network.common.Config.SWARM_MATCH_MAP, Cell = from, Position = start };
        var now = DateTime.UtcNow;
        var request = new MovementRequest(to, 1f);
        MatchMoveService.PrepareMovement(runtime, info, bot, request, now);
        MatchMoveService.PrepareMovement(runtime, info, monster, request, now, true);
        MatchMoveService.MoveAlongPath(runtime, bot, start, 10000f);
        Assert.True(bot.WaypointIndex < route.Count);
        var monsterPosition = MatchMoveService.MoveAlongPath(runtime, monster, start, 10000f);
        Assert.Equal(route.Count, monster.WaypointIndex);
        Assert.Equal(route[^1], monsterPosition);
        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        var openBot = CreatePath(route);
        MatchMoveService.PrepareMovement(runtime, info, openBot, request, now);
        var openBotPosition = MatchMoveService.MoveAlongPath(runtime, openBot, start, 10000f);
        Assert.Equal(monster.WaypointIndex, openBot.WaypointIndex);
        Assert.Equal(monsterPosition, openBotPosition);
    }

    [Fact]
    public void PreparationRejectsWallPathsForBoth()
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
        var cell = MapCoordinateConverter.WorldToCell(map, start);
        var info = new GameObjectInfo {  MapId = network.common.Config.SWARM_MATCH_MAP, Cell = cell, Position = start };
        var request = new MovementRequest(new Cell(-10000, -10000), 1f);
        MatchMoveService.PrepareMovement(runtime, info, bot, request, DateTime.UtcNow);
        MatchMoveService.PrepareMovement(runtime, info, monster, request, DateTime.UtcNow, true);
        Assert.Empty(bot.Waypoints);
        Assert.Empty(monster.Waypoints);
        Assert.Equal(start, MatchMoveService.MoveAlongPath(runtime, bot, start, 1000));
        Assert.Equal(start, MatchMoveService.MoveAlongPath(runtime, monster, start, 1000));
        Assert.Equal(0, bot.WaypointIndex);
        Assert.Equal(0, monster.WaypointIndex);
    }

    [Fact]
    public void PressureFieldDoesNotBlockMovement()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        var now = DateTime.UtcNow;
        runtime.Closures.GameStartTime = now.AddHours(-1);
        var map = Config.SWARM_MATCH_MAP;
        var from = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var to = GameMapData.GetAreaSpawnCell(map, AreaType.S2Library1);
        Assert.True(SwarmPressureField.GetDistance(to) > runtime.Closures.GetSafeDistance(now));
        var steps = MapPathfinder.FindPath(map, AreaType.S2Corridor9, from, AreaType.S2Library1, to)!;
        var path = CreatePath(steps.Select(step => MapCoordinateConverter.CellToWorld(map, step.Cell)));
        var reached = MatchMoveService.MoveAlongPath(runtime, path,
            MapCoordinateConverter.CellToWorld(map, from), 10000f);
        Assert.Equal(MapCoordinateConverter.CellToWorld(map, to), reached);
        Assert.Equal(path.Waypoints.Count, path.WaypointIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationDoesNotOverrideRequestedClosedDestination(bool ignoreClosedDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        var map = Config.SWARM_MATCH_MAP;
        var from = GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9);
        var to = GameMapData.GetAreaSpawnCell(map, AreaType.S2Library1);
        var steps = MapPathfinder.FindPath(map, AreaType.S2Corridor9, from, AreaType.S2Library1, to)!;
        var path = CreatePath(steps.Select(step => MapCoordinateConverter.CellToWorld(map, step.Cell)));
        runtime.Closures.InitializeMatching([(AreaType.S2Library1, 0)]);
        runtime.Closures.CloseDueAreas();
        var info = new GameObjectInfo { MapId = network.common.Config.SWARM_MATCH_MAP, Cell = from,
            Position = MapCoordinateConverter.CellToWorld(map, from) };
        MatchMoveService.PrepareMovement(runtime, info, path, new MovementRequest(to, 1f),
            DateTime.UtcNow, ignoreClosedDoors);
        Assert.NotEmpty(path.Waypoints);
        var position = MatchMoveService.MoveAlongPath(runtime, path,
            MapCoordinateConverter.CellToWorld(map, from), 10000f);
        Assert.Equal(MapCoordinateConverter.CellToWorld(map, to), position);
        Assert.Equal(AreaType.S2Library1, GameMapData.GetCurrentArea(map,
            MapCoordinateConverter.WorldToCell(map, position)));
    }
    [Fact]
    public void RequiresMatchLockAndKeepsPerEntityProgressIndependent()
    {
        var runtime = CreateRuntime();
        var start = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var first = CreatePath([start]);
        var second = CreatePath([start]);
        Assert.Throws<InvalidOperationException>(() => MatchMoveService.MoveAlongPath(runtime, first, start, 1));
        using var scope = runtime.Enter();
        MatchMoveService.MoveAlongPath(runtime, first, start, 1);
        Assert.Equal(1, first.WaypointIndex);
        Assert.Equal(0, second.WaypointIndex);
        first.Clear();
        Assert.Single(second.Waypoints);
    }
}
