using game_server.matches;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotMovementIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanningDoesNotMoveAndResetBeforeExecutionCancelsMovement(bool sleepBeforeExecution)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987651);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var target = new Vector3f(before.X + 0.1f, before.Y, 0f);
        var now = DateTime.UtcNow;
        bot.Movement.Waypoints.Add(target);
        bot.Movement.LastProcessedAtUtc = now.AddSeconds(-0.05);
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.SetMovementTarget(BotMovementMode.Escort, bot.Player.CurrentArea, cell);
        var behavior = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance);
        var movement = new MatchMovementService(behavior, null!, null!);

        behavior.PlanMovement(runtime, bot, now, false);
        behavior.ConfigureMovement(runtime, bot, now);
        Assert.Same(before, bot.Player.Position);
        Assert.True(bot.Movement.Speed > 0f);
        Assert.True(bot.Movement.FollowPath);
        if (sleepBeforeExecution) bot.Movement.ResetIntent();
        var result = MatchMovementService.Move(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, 0.05f);
        if (sleepBeforeExecution)
        {
            Assert.Same(before, bot.Player.Position);
        }
        else
        {
            Assert.True(result.Changed);
            Assert.True(bot.Player.Position!.X > before.X);
        }
        Assert.Equal(0f, bot.Movement.Speed);
        Assert.False(bot.Movement.FollowPath);
        var after = bot.Player.Position;
        MatchMovementService.Move(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, 0.05f);
        Assert.Equal(after, bot.Player.Position);
    }

    [Fact]
    public void DodgeCanMoveWhilePathIsPausedAndKeepsRouteProgress()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987652);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var now = DateTime.UtcNow;
        bot.Movement.Waypoints.Add(new Vector3f(before.X, before.Y + 0.1f, 0f));
        bot.Movement.Speed = 1f;
        bot.Movement.DodgeDirection = new Vector3f(1, 0, 0);
        bot.Movement.FollowPath = false;
        bot.Movement.LastProcessedAtUtc = now.AddSeconds(-0.05);
        var behavior = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance);
        var movement = new MatchMovementService(behavior, null!, null!);

        var result = MatchMovementService.Move(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, 0.05f);

        Assert.True(result.Changed);
        Assert.NotEqual(before, bot.Player.Position);
        Assert.Equal(0, bot.Movement.WaypointIndex);
        Assert.Null(bot.Movement.DodgeDirection);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BehaviorRequestsPathWithoutReplacingItBeforeMovementStage(bool canPlanThisTick)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987653);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var oldTarget = new Vector3f(-100f, -100f, 0f);
        bot.Movement.Waypoints.Add(oldTarget);
        Cell? destination = null;
        foreach (var candidate in new[] { new Cell(cell.X + 1, cell.Y), new Cell(cell.X - 1, cell.Y),
                     new Cell(cell.X, cell.Y + 1), new Cell(cell.X, cell.Y - 1) })
        {
            if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) != bot.Player.CurrentArea)
                continue;
            var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea,
                cell, bot.Player.CurrentArea, candidate);
            if (path is not { Count: > 0 })
                continue;
            destination = candidate;
            break;
        }
        Assert.NotNull(destination);
        bot.SetMovementTarget(BotMovementMode.Escort, bot.Player.CurrentArea, destination!);
        var before = bot.Player.Position;
        var behavior = new BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance);

        behavior.PlanMovement(runtime, bot, DateTime.UtcNow, canPlanThisTick);

        Assert.Equal(canPlanThisTick ? MovementPathRequest.CellPath : MovementPathRequest.None, bot.Movement.PathRequest);
        Assert.Same(oldTarget, Assert.Single(bot.Movement.Waypoints));
        Assert.Same(before, bot.Player.Position);
        Assert.Equal(0f, bot.Movement.Speed);
        if (canPlanThisTick)
        {
            Assert.True(MatchMovementService.TryPlanCellPath(runtime, bot.Player.GameInfo.ObjectInfo,
                bot.Movement, bot.Movement.DestinationArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Movement.Destination!)));
            Assert.DoesNotContain(oldTarget, bot.Movement.Waypoints);
            Assert.Equal(MovementPathRequest.None, bot.Movement.PathRequest);
            behavior.ConfigureMovement(runtime, bot, DateTime.UtcNow);
            Assert.True(bot.Movement.Speed > 0f);
        }
        bot.Movement.ResetIntent();
        Assert.Equal(MovementPathRequest.None, bot.Movement.PathRequest);
    }
}
