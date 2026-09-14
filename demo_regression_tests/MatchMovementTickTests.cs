using game_server.matches;
using game_server.matches.monsters;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchMovementTickTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickRunsBotMovementOnlyAfterStartAndAlwaysPreparesMonsters(bool started)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987631);
        var bots = new BotProbe();
        var monsters = new MonsterBehaviorService();
        var movement = new MatchMovementService(bots, monsters, new MatchMonsterSpawnService(monsters));
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        // 봇의 Player도 운영과 동일하게 참가자 목록에 등록한다.
        runtime.RegisterParticipant(runtime.Bots.GetBot(-1)!.Player);
        if (started) runtime.StartGameplay();

        movement.ProcessTick(runtime, DateTime.UtcNow);

        Assert.Equal(started ? 1 : 0, bots.Calls);
        Assert.Equal(started ? 1 : 0, bots.Decisions);
        Assert.True(runtime.Monsters.IsInitialized);
    }

    [Fact]
    public void TickRequiresLockAndDoesNotRunAfterEnd()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987632);
        var movement = new MatchMovementService(null!, null!, null!);
        Assert.Throws<InvalidOperationException>(() => movement.ProcessTick(runtime, DateTime.UtcNow));
        using var scope = runtime.Enter();
        runtime.TryMarkEnded();
        movement.ProcessTick(runtime, DateTime.UtcNow);
        Assert.False(runtime.Monsters.IsInitialized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(50)]
    public void BotMovesOnlyForPositiveElapsedTime(int elapsedMilliseconds)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987633);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        runtime.StartGameplay();
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var now = DateTime.UtcNow;
        bot.Movement.LastProcessedAtUtc = now.AddMilliseconds(-elapsedMilliseconds);
        bot.Movement.Waypoints.Add(new Vector3f(before.X + 0.1f, before.Y, 0f));
        var service = new MatchMovementService(new MovingBotProbe(), null!, null!);

        service.ProcessTick(runtime, now);

        Assert.Equal(elapsedMilliseconds > 0, !before.Equals(bot.Player.Position));
        Assert.Equal(now, bot.Movement.LastProcessedAtUtc);
        Assert.Equal(0f, bot.Movement.Speed);
        Assert.False(bot.Movement.FollowPath);
        Assert.True(float.IsFinite(bot.Player.Velocity.X));
    }

    [Fact]
    public void MissingBotPositionIsSkippedBeforeBehaviorPreparation()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987634);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        runtime.StartGameplay();
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.Position = null;
        bot.Movement.Speed = 3f;
        bot.Movement.FollowPath = true;
        var behavior = new BotProbe();
        var service = new MatchMovementService(behavior, null!, null!);

        service.ProcessTick(runtime, DateTime.UtcNow);

        Assert.Equal(0, behavior.Calls);
        Assert.Null(bot.Player.Position);
        Assert.Equal(0f, bot.Movement.Speed);
        Assert.False(bot.Movement.FollowPath);
    }

    private sealed class MovingBotProbe()
        : BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public override void PrepareMovement(MatchRuntime runtime, Bot bot, DateTime now, bool canPlanThisTick)
        {
            bot.Movement.ResetIntent();
            bot.Movement.Speed = 1f;
            bot.Movement.FollowPath = true;
        }
    }

    private sealed class BotProbe() : BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Calls { get; private set; }
        public int Decisions { get; private set; }
        public override void SelectMovementTarget(MatchRuntime runtime, Bot bot) => Decisions++;
        public override void PrepareMovement(MatchRuntime runtime, Bot bot, DateTime now, bool canPlanThisTick)
        {
            Assert.True(Monitor.IsEntered(runtime.MatchLock));
            Calls++;
            base.PrepareMovement(runtime, bot, now, canPlanThisTick);
        }
    }
}
