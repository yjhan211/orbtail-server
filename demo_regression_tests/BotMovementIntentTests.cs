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
        Assert.Same(before, bot.Player.Position);
        Assert.True(bot.Movement.Speed > 0f);
        Assert.True(bot.Movement.FollowPath);
        if (sleepBeforeExecution) bot.Movement.ResetIntent();
        var result = MatchMovementService.Move(runtime, bot.Player.Profile.ObjectInfo, bot.Movement, 0.05f);
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
        MatchMovementService.Move(runtime, bot.Player.Profile.ObjectInfo, bot.Movement, 0.05f);
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

        var result = MatchMovementService.Move(runtime, bot.Player.Profile.ObjectInfo, bot.Movement, 0.05f);

        Assert.True(result.Changed);
        Assert.NotEqual(before, bot.Player.Position);
        Assert.Equal(0, bot.Movement.WaypointIndex);
        Assert.Null(bot.Movement.DodgeDirection);
    }
}
