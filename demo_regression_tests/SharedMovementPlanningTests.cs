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
            Area = AreaType.S2Corridor9, Cell = cell,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell)
        };
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
            Area = AreaType.S2Library1, Cell = startCell,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, startCell)
        };
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var bot = new MovementState();
        var monster = new MovementState();
        Assert.True(MatchMoveService.TryPlanPath(runtime, info, bot, AreaType.S2Corridor9, destination));
        Assert.True(MatchMoveService.TryPlanPath(runtime, info, monster, AreaType.S2Corridor9, destination, true));
        var botEnd = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Waypoints[^1]);
        Assert.NotEqual(destination, botEnd);
        Assert.Contains(GameInteractableData.GetAll(), item =>
            item.DoorId > 0 &&
            item.CellX == botEnd.X && item.CellY == botEnd.Y);
        Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, destination), monster.Waypoints[^1]);

        foreach (var door in GameDoorData.GetAll()) runtime.Doors.OpenDoor(door.DoorId);
        Assert.True(MatchMoveService.TryPlanPath(runtime, info, bot, AreaType.S2Corridor9, destination));
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
        var state = new MovementState { WaypointIndex = 1, FollowPath = true };
        state.Waypoints.Add(info.Position);
        state.Waypoints.Add(new Vector3f(info.Position.X + 0.1f, info.Position.Y, 0f));
        var before = state.Waypoints.ToArray();
        Assert.False(MatchMoveService.TryPlanPath(runtime, info, state, AreaType.None, new Cell(-10000, -10000), ignoreDoors));
        Assert.Equal(before, state.Waypoints);
        Assert.Equal(1, state.WaypointIndex);
        Assert.True(state.FollowPath);
    }

    [Fact]
    public void CandidateSearchRejectsUnsafeIntermediateCell()
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var destination = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, info.Area, info.Cell, AreaType.S2Library1, destination)!;
        var blockedCell = path[path.Count / 2].Cell;
        Assert.NotEqual(destination, blockedCell);
        Assert.False(MatchMoveService.TrySelectReachableCell(runtime, info, [destination],
            cell => !cell.Equals(blockedCell), _ => false, out _));
        Assert.True(MatchMoveService.TrySelectReachableCell(runtime, info, [destination],
            _ => true, _ => false, out var selected));
        Assert.Equal(destination, selected);
    }

    [Fact]
    public void PlanningRequiresLockAndDoesNotReplacePathAfterEnd()
    {
        var runtime = CreateRuntime();
        var info = CreateObject();
        var state = new MovementState();
        state.Waypoints.Add(info.Position);
        Assert.Throws<InvalidOperationException>(() =>
            MatchMoveService.TryPlanPath(runtime, info, state, info.Area, info.Cell));
        using var scope = runtime.Enter();
        runtime.TryMarkEnded();
        Assert.False(MatchMoveService.TryPlanPath(runtime, info, state, info.Area, info.Cell));
        Assert.Single(state.Waypoints);
        Assert.False(MatchMoveService.TrySelectReachableCell(runtime, info, [info.Cell],
            _ => true, _ => false, out _));
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
        Assert.True(state.FollowPath);
        Assert.NotEmpty(state.Waypoints);
        var result = MovementPreparationTestSteps.Advance(runtime, info, state, request, 1f, ignoreDoors, now);
        Assert.True(result.ReachedDestination);
        Assert.Equal(destination, info.Cell);
        Assert.Empty(state.Waypoints);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now, ignoreDoors);
        Assert.False(state.FollowPath);
        var repeated = MovementPreparationTestSteps.Advance(runtime, info, state, request, 1f, ignoreDoors, now);
        Assert.False(repeated.ReachedDestination);
        Assert.True(state.ReachedDestination);
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
        Assert.False(state.FollowPath);
        MatchMoveService.PrepareMovement(runtime, info, state, request, now.AddTicks(1), ignoreDoors);
        Assert.Equal(retryAt, state.NextPathPlanAtUtc);
        MatchMoveService.PrepareMovement(runtime, info, state, request, retryAt, ignoreDoors);
        Assert.True(state.NextPathPlanAtUtc > retryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommonHoldKeepsSafePathAndCancelsExecution(bool ignoreDoors)
    {
        var runtime = CreateRuntime();
        using var scope = runtime.Enter();
        var info = CreateObject();
        var state = new MovementState { FollowPath = true };
        state.Waypoints.Add(info.Position);
        MatchMoveService.PrepareMovement(runtime, info, state,
            new MovementRequest(null, 10f, HoldPosition: true), DateTime.UtcNow, ignoreDoors);
        Assert.Single(state.Waypoints);
        Assert.False(state.FollowPath);
        var before = info.Position;
        var result = MovementPreparationTestSteps.Advance(runtime, info, state,
            new MovementRequest(null, 10f, HoldPosition: true), 1f, ignoreDoors);
        Assert.Equal(before, info.Position);
        Assert.False(result.ReachedDestination);
        Assert.Single(state.Waypoints);
    }
}
