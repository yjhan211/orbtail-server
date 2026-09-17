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
    public void PlanningDoesNotMoveAndHoldRequestCancelsMovement(bool sleepBeforeExecution)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987651);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var target = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(before.X + 1f, before.Y, 0f));
        var now = DateTime.UtcNow;
        bot.Movement.Waypoints.Add(target);
        bot.Movement.LastProcessedAtUtc = now.AddSeconds(-0.05);
        var behavior = new FixedTargetBehavior(target);
        var movement = new MatchMoveService(behavior, null!);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, now);
        Assert.Same(before, bot.Player.Position);
        Assert.True(request.Speed > 0f);
        if (sleepBeforeExecution) request = request with { Speed = 0f };
        var result = MovementPreparationTestSteps.Advance(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, request, 0.05f);
        if (sleepBeforeExecution)
        {
            Assert.Same(before, bot.Player.Position);
        }
        else
        {
            Assert.True(result.Changed);
            Assert.True(bot.Player.Position!.X > before.X);
        }
        var after = bot.Player.Position;
        request = request with { Speed = 0f };
        MovementPreparationTestSteps.Advance(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, request, 0.05f);
        Assert.Equal(after, bot.Player.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationReplansOnlyOnItsTurnAndMakesMovementReady(bool canPlanThisTick)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987653);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var oldTarget = cell.Clone();
        bot.Movement.NextPathPlanAtUtc = canPlanThisTick ? DateTime.MinValue : DateTime.MaxValue;
        bot.Movement.Waypoints.Add(oldTarget);
        Cell? destination = null;
        foreach (var candidate in new[] { new Cell(cell.X + 1, cell.Y), new Cell(cell.X - 1, cell.Y),
                     new Cell(cell.X, cell.Y + 1), new Cell(cell.X, cell.Y - 1) })
        {
            if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) != GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell))
                continue;
            var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell),
                cell, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell), candidate);
            if (path is not { Count: > 0 })
                continue;
            destination = candidate;
            break;
        }
        Assert.NotNull(destination);
        var before = bot.Player.Position;
        var behavior = new FixedTargetBehavior(destination);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, DateTime.UtcNow);

        Assert.Same(before, bot.Player.Position);
        Assert.True(request.Speed > 0f);
        if (canPlanThisTick)
        {
            Assert.All(bot.Movement.Waypoints, waypoint => Assert.NotSame(oldTarget, waypoint));
            Assert.NotEmpty(bot.Movement.Waypoints);
        }
        else
        {
            Assert.Same(oldTarget, Assert.Single(bot.Movement.Waypoints));
        }
    }
    [Fact]
    public void SleepingBotClearsPathWithoutChoosingTarget()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987654);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        bot.Player.State = PlayerState.SLEEP;
        var target = new Cell(cell.X + 1, cell.Y);
        bot.Movement.Waypoints.Add(target);
        var behavior = new FixedTargetBehavior(target);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, DateTime.UtcNow);

        Assert.Equal(0, behavior.Selections);
        Assert.Empty(bot.Movement.Waypoints);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DamageAndAvoidanceDoNotBypassCommonReplanDeadline(bool underFire, bool avoidingNow)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987655);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var now = DateTime.UtcNow;
        var deadline = now.AddSeconds(1);
        var destination = cell.GetAdjacentCells().First(candidate =>
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) == GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell) &&
            MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell), cell, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell), candidate) is { Count: > 0 });
        bot.Movement.Waypoints.Add(destination);
        bot.Movement.NextPathPlanAtUtc = deadline;
        bot.LastDamagedAtUtc = underFire ? now : now.AddMinutes(-1);
        bot.MonsterAvoidanceTarget = avoidingNow ? (bot.Player.Cell!, now) : null;

        var request = MovementPreparationTestSteps.Bot(new FixedTargetBehavior(destination), runtime, bot, now);

        Assert.Equal(deadline, bot.Movement.NextPathPlanAtUtc);
    }

    [Fact]
    public void HealthAndOrbChangesUpdateBotStateWithoutMovementPreparation()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(987657);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var health = TestGameSessionServices.CreateHealthService(store);
        bot.Player.Health = Config.MAX_HEALTH;
        bot.Player.State = PlayerState.SLEEP;
        health.ApplyDamage(runtime, bot.Player, Config.MAX_HEALTH - 1, handleElimination: false);
        Assert.True(bot.Wounded);
        health.Recover(runtime, bot.Player, Config.MAX_HEALTH);
        Assert.False(bot.Wounded);

        bot.Player.Orbs.TakeAllOrbs();
        bot.Player.Orbs.AddOrb(107000020);
        bot.Player.Orbs.AddOrb(107000020);
        var trails = new game_server.players.PlayerOrbTrailService();
        trails.DestroyOrbsFromOrdinal(runtime, bot.Player, 1);
        Assert.Equal(DateTime.MinValue, bot.SwarmBareSpeedUntilUtc);
        var before = DateTime.UtcNow;
        trails.DestroyOrbsFromOrdinal(runtime, bot.Player, 0);
        Assert.InRange(bot.SwarmBareSpeedUntilUtc,
            before.AddSeconds(Config.SWARM_BARE_MOVE_SPEED_SECONDS),
            DateTime.UtcNow.AddSeconds(Config.SWARM_BARE_MOVE_SPEED_SECONDS));
        var speedUntil = bot.SwarmBareSpeedUntilUtc;
        trails.DestroyOrbsFromOrdinal(runtime, bot.Player, 0);
        Assert.Equal(speedUntil, bot.SwarmBareSpeedUntilUtc);
        bot.Player.State = PlayerState.IDLE;
        var request = MovementPreparationTestSteps.Bot(new FixedTargetBehavior(null), runtime, bot, DateTime.UtcNow);
        Assert.Equal(speedUntil, bot.SwarmBareSpeedUntilUtc);
    }

    private sealed class FixedTargetBehavior(Cell? destination)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Selections { get; private set; }
        private readonly Cell? _destination = destination;
        public override Cell? SelectMovementTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
        {
            Selections++;
            return _destination;
        }
    }
}
