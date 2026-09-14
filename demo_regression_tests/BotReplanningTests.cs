using game_server.matches;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotReplanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinishedPathDoesNotWaitForDeadlineButStillWaitsForPlanningTurn(bool planningTurn)
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        var deadline = now.AddSeconds(1);
        bot.Movement.NextPathPlanAtUtc = deadline;
        new TargetBehavior(bot.Player.CurrentArea, destination).PrepareMovement(runtime, bot, now, planningTurn);
        if (planningTurn)
        {
            Assert.NotEmpty(bot.Movement.Waypoints);
            Assert.True(bot.Movement.FollowPath);
            Assert.Equal(now.AddSeconds(1.5), bot.Movement.NextPathPlanAtUtc);
        }
        else
        {
            Assert.Empty(bot.Movement.Waypoints);
            Assert.False(bot.Movement.FollowPath);
            Assert.Equal(deadline, bot.Movement.NextPathPlanAtUtc);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPlanPreservesOnlyValidOldPathAndRetriesNextTurn(bool validPath)
    {
        var (runtime, bot, destination) = CreateBot();
        using var scope = runtime.Enter();
        var waypoint = validPath
            ? MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, destination)
            : new Vector3f(-10000f, -10000f, 0f);
        bot.Movement.Waypoints.Add(waypoint);
        new TargetBehavior(AreaType.None, new Cell(-10000, -10000)).PrepareMovement(runtime, bot, DateTime.UtcNow, true);
        Assert.Equal(DateTime.MinValue, bot.Movement.NextPathPlanAtUtc);
        Assert.Equal(validPath, bot.Movement.FollowPath);
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
        bot.Movement.Waypoints.Add(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, destination));
        new TargetBehavior(bot.Player.CurrentArea, bot.Player.Cell!, true).PrepareMovement(runtime, bot, DateTime.UtcNow, true);
        Assert.Empty(bot.Movement.Waypoints);
        Assert.False(bot.Movement.FollowPath);
        Assert.Equal(0f, bot.Movement.Speed);
    }

    [Fact]
    public void WanderSelectsReachableNearbyCellAndKeepsDestination()
    {
        var (runtime, bot, _) = CreateBot();
        using var scope = runtime.Enter();
        bot.Player.Orbs.TakeAllItems();
        var behavior = new BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance);
        behavior.SelectMovementTarget(runtime, bot);
        Assert.False(bot.Movement.HoldPosition);
        var targetArea = bot.Movement.DestinationArea;
        var targetCell = bot.Movement.DestinationCell!.Clone();
        Assert.Equal(bot.Player.CurrentArea, targetArea);
        int radius = Config.SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS;
        Assert.InRange(bot.Player.Cell!.GetDistance(targetCell), Math.Max(2, radius / 2), radius);
        Assert.False(runtime.Closures.IsAreaClosed(targetArea));
        Assert.NotEmpty(MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
            bot.Player.Cell!, targetArea, targetCell)!);
        for (int i = 0; i < 10; i++)
        {
            behavior.SelectMovementTarget(runtime, bot);
            Assert.Equal(targetArea, bot.Movement.DestinationArea);
            Assert.Equal(0, targetCell.GetDistance(bot.Movement.DestinationCell!));
        }
    }

    [Fact]
    public void WanderWithoutValidAreaHoldsPosition()
    {
        var (runtime, bot, _) = CreateBot();
        using var scope = runtime.Enter();
        bot.Player.CurrentArea = AreaType.None;
        BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.True(bot.Movement.HoldPosition);
        Assert.Null(bot.ExplorationTarget);
        Assert.Equal(0, bot.Player.Cell!.GetDistance(bot.Movement.DestinationCell!));
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
        BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.False(bot.Movement.HoldPosition);
        Assert.Equal(bot.Player.CurrentArea, bot.Movement.DestinationArea);
        int radius = Config.SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS;
        var destination = bot.Movement.DestinationCell!.Clone();
        Assert.InRange(bot.Player.Cell!.GetDistance(destination), Math.Max(2, radius / 2), radius);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
            bot.Player.Cell, bot.Player.CurrentArea, destination);
        Assert.NotNull(path);
        Assert.NotEmpty(path!);
        double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        Assert.All(path!, step => Assert.True(SwarmPressureField.GetDistance(step.Cell) <= safeDistance));
        BotBehaviorService.SelectWanderTarget(runtime, bot, DateTime.UtcNow);
        Assert.Equal(0, destination.GetDistance(bot.Movement.DestinationCell!));
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
        bot.LoopWaitUntil = DateTime.MinValue;
        var destination = cell.GetAdjacentCells().First(candidate =>
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) == area &&
            MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, area, cell, area, candidate) is { Count: > 0 });
        return (runtime, bot, destination);
    }

    private sealed class TargetBehavior(AreaType area, Cell destination, bool hold = false)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public override void SelectMovementTarget(MatchRuntime runtime, Bot bot)
        {
            bot.SetMovementTarget(area, destination);
            bot.Movement.HoldPosition = hold;
        }
    }
}
