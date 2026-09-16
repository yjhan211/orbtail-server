using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SharedMovementPlanningTests
{
    private static MatchRuntime CreateRuntime()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        return TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987680);
    }

    private static GameObjectInfo CreateObject()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        return new GameObjectInfo
        {
            MapId = network.common.Config.SWARM_MATCH_MAP, Cell = cell,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell)
        };
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0f)]
    public void PreparationDiscardsInvalidRoute(float speed)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var now = DateTime.UtcNow;
        var deadline = now.AddMinutes(1);
        var state = new MovementState { NextPathPlanAtUtc = deadline };
        var blocked = new Cell(-10000, -10000);
        state.Waypoints.Add(blocked);
        var request = new MovementRequest(blocked, speed);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now);
        var result = MovementPreparationTestSteps.Advance(runtime, info, state, request, 0.05f, nowUtc: now);
        Assert.False(result.Changed);
        if (speed <= 0f)
        {
            Assert.Empty(state.Waypoints);
            Assert.Equal(DateTime.MinValue, state.NextPathPlanAtUtc);
            return;
        }
        Assert.Empty(state.Waypoints);
        Assert.Equal(0, state.WaypointIndex);
        Assert.Equal(now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS), state.NextPathPlanAtUtc);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopRequestDoesNotPlanOrMoveToCellCenter(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        info.Position = new Vector3f(info.Position.X + 0.01f, info.Position.Y, 0f);
        Assert.Equal(info.Cell, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, info.Position));
        var position = info.Position;
        var state = new MovementState();
        var now = DateTime.UtcNow;
        var request = new MovementRequest(info.Cell.Clone(), 0f);

        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);

        Assert.Empty(state.Waypoints);
        Assert.Equal(DateTime.MinValue, state.NextPathPlanAtUtc);
        MovementPreparationTestSteps.Advance(runtime, info, state, request, 0.05f, ignoreDoors, now);
        Assert.Same(position, info.Position);
    }
    [Theory]
    [InlineData(0f, false)]
    [InlineData(-1f, false)]
    [InlineData(0f, true)]
    [InlineData(-1f, true)]
    public void StoppedPathIsClearedAndReplannedOnResume(float stoppedSpeed, bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var now = DateTime.UtcNow;
        var deadline = now.AddMinutes(1);
        var state = new MovementState { NextPathPlanAtUtc = deadline };
        var invalidCell = new Cell(-10000, -10000);
        state.Waypoints.Add(invalidCell);
        var destination = info.Cell.GetAdjacentCells().First(cell =>
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == info.Area &&
            MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, info.Area, info.Cell, info.Area, cell) is { Count: > 0 });
        var request = new MovementRequest(destination, stoppedSpeed);

        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);

        Assert.Empty(state.Waypoints);
        Assert.Equal(DateTime.MinValue, state.NextPathPlanAtUtc);
        Assert.Equal(0, state.WaypointIndex);
        var position = info.Position;
        MovementPreparationTestSteps.Advance(runtime, info, state, request, 0.05f, ignoreDoors, now);
        Assert.Equal(position, info.Position);

        request = request with { Speed = 1f };
        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);

        Assert.DoesNotContain(invalidCell, state.Waypoints);
        Assert.NotEmpty(state.Waypoints);
        Assert.True(state.NextPathPlanAtUtc < deadline);
    }
    [Fact]
    public void SamePlannerStopsBotAtDoorButLetsMonsterReachDestination()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        runtime.Doors.CloseDoorsForAreas(GameDoorData.GetAll().Select(door => door.AreaType));
        var startCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
        var info = new GameObjectInfo
        {
            MapId = network.common.Config.SWARM_MATCH_MAP, Cell = startCell,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, startCell)
        };
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var bot = new MovementState();
        var monster = new MovementState();
        var now = DateTime.UtcNow;
        var request = new MovementRequest(destination, 1f);
        MatchMoveService.PrepareMovement(runtime, info, bot, request, now);
        MatchMoveService.PrepareMovement(runtime, info, monster, request, now, true);
        Assert.NotEmpty(bot.Waypoints);
        Assert.NotEmpty(monster.Waypoints);
        Assert.Equal(now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS), bot.NextPathPlanAtUtc);
        var botEnd = bot.Waypoints[^1];
        Assert.NotEqual(destination, botEnd);
        Assert.Contains(GameInteractableData.GetAll(), item =>
            item.DoorId > 0 &&
            item.CellX == botEnd.X && item.CellY == botEnd.Y);
        Assert.Equal(destination, monster.Waypoints[^1]);

        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        MatchMoveService.PrepareMovement(runtime, info, bot, request, bot.NextPathPlanAtUtc);
        Assert.Equal(monster.Waypoints, bot.Waypoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPlanPreservesExistingPathAndIntent(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var state = new MovementState { WaypointIndex = 1 };
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, info.Position));
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(info.Position.X + 0.1f, info.Position.Y, 0f)));
        var before = state.Waypoints.ToArray();
        var now = DateTime.UtcNow;
        MatchMoveService.PrepareMovement(runtime, info, state,
            new MovementRequest(new Cell(-10000, -10000), 1f), now, ignoreDoors);
        Assert.Equal(now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS), state.NextPathPlanAtUtc);
        Assert.Equal(before, state.Waypoints);
        Assert.Equal(1, state.WaypointIndex);
    }

    [Fact]
    public void SafePathReturnsIntermediateCellsAndRejectsClosedDestination()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
        Assert.True(MatchMoveService.TryFindSafePath(runtime, info, destination, DateTime.UtcNow, out var path));
        Assert.True(path.Count > 1);
        Assert.Equal(destination, path[^1].Cell);
        Assert.All(path, step => Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, step.Cell)));
        runtime.Closures.InitializeMatching([(AreaType.S2Library1, 0)]);
        runtime.Closures.CloseDueAreas();
        Assert.False(MatchMoveService.TryFindSafePath(runtime, info, destination, DateTime.UtcNow, out var blocked));
        Assert.Empty(blocked);
    }
    [Fact]
    public void SafePathAllowsLeavingClosedOrigin()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
        runtime.Closures.InitializeMatching([(info.Area, 0)]);
        runtime.Closures.CloseDueAreas();

        Assert.True(MatchMoveService.TryFindSafePath(runtime, info, destination, DateTime.UtcNow, out var path));
        Assert.Equal(destination, path[^1].Cell);
    }

    [Fact]
    public void FindPathRejectsBlockedDestination()
    {
        var runtime = CreateRuntime();
        var info = CreateObject();
        var area = AreaType.S2Library1;
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area);
        var blockedAreas = new HashSet<AreaType> { area };

        Assert.NotNull(MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, info.Area, info.Cell, area, destination));
        Assert.Null(MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, info.Area, info.Cell, area, destination, blockedAreas));
        Assert.Contains(area, blockedAreas);
    }

    [Fact]
    public void FindPathRejectsBlockedIntermediateAreas()
    {
        var runtime = CreateRuntime();
        var info = CreateObject();
        var mapId = Config.SWARM_MATCH_MAP;
        foreach (var region in GameMapData.GetAreas(mapId))
        {
            var destination = GameMapData.GetAreaSpawnCell(mapId, region.AreaType);
            var path = MapPathfinder.FindPath(mapId, info.Area, info.Cell, region.AreaType, destination);
            if (path == null || !path.Any(step => step.Area != info.Area && step.Area != region.AreaType))
            {
                continue;
            }
            var blockedAreas = new HashSet<AreaType>();
            foreach (var candidate in GameMapData.GetAreas(mapId))
            {
                if (candidate.AreaType != info.Area && candidate.AreaType != region.AreaType)
                {
                    blockedAreas.Add(candidate.AreaType);
                }
            }
            Assert.Null(MapPathfinder.FindPath(mapId, info.Area, info.Cell, region.AreaType, destination, blockedAreas));
            return;
        }
        Assert.Fail("Test map must contain a route through an intermediate area.");
    }

    [Fact]
    public void OpenDestinationCanBeReachedThroughClosedIntermediateAreas()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var map = Config.SWARM_MATCH_MAP;
        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        foreach (var region in GameMapData.GetAreas(map))
        {
            var destination = GameMapData.GetAreaSpawnCell(map, region.AreaType);
            var original = MapPathfinder.FindPath(map, info.Area, info.Cell, region.AreaType, destination);
            if (original == null) continue;
            var intermediate = original.Select(step => step.Area)
                .Where(area => area != info.Area && area != region.AreaType && area != AreaType.None)
                .Distinct().ToArray();
            if (intermediate.Length == 0) continue;
            runtime.Closures.InitializeMatching(intermediate.Select(area => (area, 0)).ToArray());
            runtime.Closures.CloseDueAreas();
            var now = DateTime.UtcNow;
            Assert.True(MatchMoveService.TryFindSafePath(runtime, info, destination, now, out _));
            var state = new MovementState();
            MatchMoveService.PrepareMovement(runtime, info, state, new MovementRequest(destination, 1f), now);
            Assert.NotEmpty(state.Waypoints);
            Assert.Contains(state.Waypoints, cell => runtime.Closures.IsAreaClosed(GameMapData.GetCurrentArea(map, cell)));
            var reached = MatchMoveService.MoveAlongPath(runtime, state, info.Position, 10000f, now);
            Assert.Equal(destination, MapCoordinateConverter.WorldToCell(map, reached));
            return;
        }
        Assert.Fail("Test map must contain a route through an intermediate area.");
    }

    [Fact]
    public void PlanningRequiresLockAndDoesNotReplacePathAfterEnd()
    {
        var runtime = CreateRuntime();
        var info = CreateObject();
        var state = new MovementState();
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, info.Position));
        Assert.Throws<InvalidOperationException>(() =>
            MatchMoveService.PrepareMovement(runtime, info, state, new MovementRequest(info.Cell, 1f), DateTime.UtcNow));
        using var scope = runtime.Enter();
        runtime.TryMarkEnded();
        MatchMoveService.PrepareMovement(runtime, info, state, new MovementRequest(info.Cell, 1f), DateTime.UtcNow);
        Assert.Equal(DateTime.MinValue, state.NextPathPlanAtUtc);
        Assert.Single(state.Waypoints);
        Assert.False(MatchMoveService.TryFindSafePath(runtime, info, info.Cell, DateTime.UtcNow, out _));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonPreparationPlansAndReportsArrivalOnce(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var destination = info.Cell.GetAdjacentCells().First(cell =>
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == info.Area &&
            GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell));
        var state = new MovementState();
        var now = DateTime.UtcNow;
        var request = new MovementRequest(destination, 10f);
        var before = info.Position;
        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);
        Assert.Same(before, info.Position);
        Assert.NotEmpty(state.Waypoints);
        var result = MovementPreparationTestSteps.Advance(runtime, info, state, request, 1f, ignoreDoors, now);
        Assert.True(result.ReachedPathEnd);
        Assert.Equal(destination, info.Cell);
        Assert.Empty(state.Waypoints);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);
        var repeated = MovementPreparationTestSteps.Advance(runtime, info, state, request, 1f, ignoreDoors, now);
        Assert.False(repeated.ReachedPathEnd);
        Assert.Empty(state.Waypoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonPreparationBacksOffAfterFailedPath(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var state = new MovementState();
        var now = DateTime.UtcNow;
        var request = new MovementRequest(new Cell(-10000, -10000), 1f);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);
        var retryAt = state.NextPathPlanAtUtc;
        Assert.True(retryAt > now);
        Assert.Empty(state.Waypoints);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now.AddTicks(1), ignoreDoors);
        Assert.Equal(retryAt, state.NextPathPlanAtUtc);
        MatchMoveService.PrepareMovement(runtime, info, state, request, retryAt, ignoreDoors);
        Assert.True(state.NextPathPlanAtUtc > retryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonStopClearsSafePathAndCancelsExecution(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var state = new MovementState();
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, info.Position));
        MatchMoveService.PrepareMovement(runtime, info, state,
            new MovementRequest(null, 0f), DateTime.UtcNow, ignoreDoors);
        Assert.Empty(state.Waypoints);
        var before = info.Position;
        var result = MovementPreparationTestSteps.Advance(runtime, info, state,
            new MovementRequest(null, 0f), 1f, ignoreDoors);
        Assert.Equal(before, info.Position);
        Assert.False(result.ReachedPathEnd);
        Assert.Empty(state.Waypoints);
    }
}
