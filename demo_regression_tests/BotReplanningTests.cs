using game_server.matches;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotReplanningTests
{
    [Fact]
    public void FirstRequestPlansImmediatelyWithDefaultDeadline()
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        Assert.Equal(DateTime.MinValue, bot.Movement.NextPathPlanAtUtc);
        MovementPreparationTestSteps.Bot(new TargetBehavior(destination), runtime, bot, now);
        Assert.NotEmpty(bot.Movement.Waypoints);
        Assert.Equal(now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS), bot.Movement.NextPathPlanAtUtc);
    }

    [Fact]
    public void FailedPathRetriesLatestRequestOnlyAfterDeadline()
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        var info = bot.Player.GameInfo.ObjectInfo;
        var movement = bot.Movement;
        MatchMoveService.PrepareMovement(runtime, info, movement,
            new MovementRequest(new Cell(-10000, -10000), 1f), now);
        Assert.Empty(movement.Waypoints);
        var deadline = movement.NextPathPlanAtUtc;
        Assert.True(deadline > now);

        var request = new MovementRequest(destination, 1f);
        MatchMoveService.PrepareMovement(runtime, info, movement, request, now);
        Assert.Empty(movement.Waypoints);
        Assert.Equal(deadline, movement.NextPathPlanAtUtc);

        MatchMoveService.PrepareMovement(runtime, info, movement, request, deadline);
        Assert.NotEmpty(movement.Waypoints);
        Assert.Equal(destination, movement.Waypoints[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPlanPreservesOnlyValidOldPathAndRetriesNextTurn(bool validPath)
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        var waypoint = validPath
            ? destination
            : new Cell(-10000, -10000);
        bot.Movement.Waypoints.Add(waypoint);
        MovementPreparationTestSteps.Bot(new TargetBehavior(new Cell(-10000, -10000)), runtime, bot, DateTime.UtcNow);
        Assert.True(bot.Movement.NextPathPlanAtUtc > DateTime.UtcNow);
        if (validPath)
        {
            Assert.Same(waypoint, Assert.Single(bot.Movement.Waypoints));
        }
        else
        {
            Assert.Empty(bot.Movement.Waypoints);
        }
    }

    [Fact]
    public void ExplicitHoldStopsOldPath()
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        bot.Movement.Waypoints.Add(destination);
        var request = MovementPreparationTestSteps.Bot(new TargetBehavior(bot.Player.Cell!, true), runtime, bot, DateTime.UtcNow);
        Assert.Equal(0f, request.Speed);
        var position = bot.Player.Position;
        MovementPreparationTestSteps.Advance(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, request, 0.05f);
        Assert.Equal(position, bot.Player.Position);
        Assert.Single(bot.Movement.Waypoints);
    }

    [Fact]
    public void WanderSelectsReachableNearbyCellAndKeepsDestination()
    {
        var (runtime, bot, _) = CreateBot();
        using var scope = runtime.Enter();
        bot.Player.Orbs.TakeAllItems();
        var behavior = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);
        var selected = behavior.SelectMovementTarget(runtime, bot, DateTime.UtcNow);
        Assert.False(selected!.Equals(bot.Player.Cell));
        var targetArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, selected!);
        var targetCell = selected!.Clone();
        Assert.Equal(bot.Player.CurrentArea, targetArea);
        int radius = Config.SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS;
        Assert.InRange(bot.Player.Cell!.GetDistance(targetCell), Math.Max(2, radius / 2), radius);
        Assert.False(runtime.Closures.IsAreaClosed(targetArea));
        Assert.NotEmpty(MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
            bot.Player.Cell!, targetArea, targetCell)!);
        for (int i = 0; i < 10; i++)
        {
            selected = behavior.SelectMovementTarget(runtime, bot, DateTime.UtcNow);
            Assert.Equal(targetArea, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, selected!));
            Assert.Equal(0, targetCell.GetDistance(selected!));
        }
    }

    [Fact]
    public void WanderWithoutValidAreaHoldsPosition()
    {
        var (runtime, bot, _) = CreateBot();
        using var scope = runtime.Enter();
        bot.Player.CurrentArea = AreaType.None;
        var selected = BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.True(selected!.Equals(bot.Player.Cell));
        Assert.Null(bot.ExplorationTarget);
        Assert.Equal(0, bot.Player.Cell!.GetDistance(selected!));
    }

    [Fact]
    public void WanderStaysInCurrentAreaWhenAdjacentAreasAreClosed()
    {
        var (runtime, bot, _) = CreateBot();
        using var scope = runtime.Enter();
        var adjacentAreas = GameAreaConnectionData.GetConnections(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea)
            .Select(connection => connection.ToArea).Distinct().ToArray();
        Assert.NotEmpty(adjacentAreas);
        runtime.Closures.Release();
        runtime.Closures.InitializeMatching(adjacentAreas.Select(area => (area, 0)).ToArray());
        runtime.Closures.CloseDueAreas();
        var selected = BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.False(selected!.Equals(bot.Player.Cell));
        Assert.Equal(bot.Player.CurrentArea, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, selected!));
        int radius = Config.SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS;
        var destination = selected!.Clone();
        Assert.InRange(bot.Player.Cell!.GetDistance(destination), Math.Max(2, radius / 2), radius);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
            bot.Player.Cell, bot.Player.CurrentArea, destination);
        Assert.NotNull(path);
        Assert.NotEmpty(path!);
        double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        Assert.All(path!, step => Assert.True(SwarmPressureField.GetDistance(step.Cell) <= safeDistance));
        selected = BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.Equal(0, destination.GetDistance(selected!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementRequestPreservesExistingPathAndUsesSuppliedTime(bool hasTarget)
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        bot.Movement.Waypoints.Add(destination);
        bot.Movement.NextPathPlanAtUtc = DateTime.UtcNow.AddMinutes(1);
        var savedDeadline = bot.Movement.NextPathPlanAtUtc;
        var nowUtc = DateTime.UtcNow.AddSeconds(-5);
        var behavior = new TargetBehavior(hasTarget ? destination : null);

        var request = behavior.CreateMovementRequest(runtime, bot, nowUtc);

        Assert.Equal(nowUtc, behavior.SelectedAtUtc);
        Assert.Same(destination, Assert.Single(bot.Movement.Waypoints));
        Assert.Equal(savedDeadline, bot.Movement.NextPathPlanAtUtc);
        Assert.Equal(hasTarget, request.Speed > 0f);
        Assert.Equal(hasTarget ? destination : null, request.DestinationCell);
    }

    private static (MatchRuntime Runtime, Bot Bot, Cell Destination) CreateBot()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(981250);
        using var scope = runtime.Enter();
        var area = AreaType.S2Corridor9;
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        runtime.RegisterParticipant(bot.Player);
        var destination = cell.GetAdjacentCells().First(candidate =>
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) == area &&
            MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, area, cell, area, candidate) is { Count: > 0 });
        return (runtime, bot, destination);
    }

    private sealed class TargetBehavior(Cell? destination, bool hold = false)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public DateTime SelectedAtUtc { get; private set; }
        public override Cell? SelectMovementTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
        {
            SelectedAtUtc = nowUtc;
            return hold ? bot.Player.Cell : destination;
        }
    }
}
