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
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.SetMovementTarget(bot.Player.CurrentArea, target);
        var behavior = new FixedTargetBehavior(bot);
        var movement = new MatchMoveService(behavior, null!);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, now);
        Assert.Same(before, bot.Player.Position);
        Assert.True(request.Speed > 0f);
        if (sleepBeforeExecution) request = request with { HoldPosition = true };
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
        request = request with { HoldPosition = true };
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
        var oldTarget = new Cell(-100, -100);
        bot.Movement.NextPathPlanAtUtc = canPlanThisTick ? DateTime.MinValue : DateTime.MaxValue;
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
        bot.SetMovementTarget(bot.Player.CurrentArea, destination!);
        bot.LoopWaitUntil = DateTime.MinValue;
        var before = bot.Player.Position;
        var behavior = new FixedTargetBehavior(bot);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, DateTime.UtcNow);

        Assert.Same(before, bot.Player.Position);
        Assert.True(request.Speed > 0f);
        if (canPlanThisTick)
        {
            Assert.DoesNotContain(oldTarget, bot.Movement.Waypoints);
            Assert.NotEmpty(bot.Movement.Waypoints);
        }
        else
        {
            Assert.Same(oldTarget, Assert.Single(bot.Movement.Waypoints));
        }
    }
    [Fact]
    public void SleepingBotClearsIntentWithoutChoosingTargetOrDiscardingPath()
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
        var behavior = new FixedTargetBehavior(bot);

        var request = MovementPreparationTestSteps.Bot(behavior, runtime, bot, DateTime.UtcNow);

        Assert.Equal(0, behavior.Selections);
        Assert.Same(target, Assert.Single(bot.Movement.Waypoints));
        Assert.Null(request.IsSafeCell);
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
            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate) == bot.Player.CurrentArea &&
            MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea, cell, bot.Player.CurrentArea, candidate) is { Count: > 0 });
        bot.SetMovementTarget(bot.Player.CurrentArea, destination);
        bot.Movement.Waypoints.Add(destination);
        bot.Movement.NextPathPlanAtUtc = deadline;
        bot.LastDamagedAtUtc = underFire ? now : now.AddMinutes(-1);
        bot.MonsterAvoidanceTarget = avoidingNow ? (bot.Player.Cell!, now) : null;

        var request = MovementPreparationTestSteps.Bot(new FixedTargetBehavior(bot), runtime, bot, now);

        Assert.Equal(deadline, bot.Movement.NextPathPlanAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DodgeWithoutNewAdviceKeepsCellOnlyUntilHoldExpires(bool holding)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987656);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var now = DateTime.UtcNow;
        var destination = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(before.X + 1f, before.Y, 0f));
        bot.SetMovementTarget(bot.Player.CurrentArea, destination);
        bot.Movement.Waypoints.Add(destination);
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.DodgeTargetCell = cell.Clone();
        bot.SwarmDodgeHoldUntilUtc = holding ? now.AddSeconds(1) : now;

        var request = MovementPreparationTestSteps.Bot(new FixedTargetBehavior(bot), runtime, bot, now);

        Assert.True(request.Speed > 0f);
        if (holding)
        {
            Assert.NotNull(request.IsSafeCell);
            Assert.Equal(cell, request.DestinationCell);
        }
        else
        {
            Assert.Null(request.IsSafeCell);
        }
        var result = MovementPreparationTestSteps.Advance(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, request, 0.01f);
        if (!holding) Assert.True(result.Changed);
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

        bot.Player.Orbs.TakeAllItems();
        bot.Player.Orbs.AddItem(107000020);
        bot.Player.Orbs.AddItem(107000020);
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
        bot.Movement.DestinationCell = null;
        var request = MovementPreparationTestSteps.Bot(new FixedTargetBehavior(bot), runtime, bot, DateTime.UtcNow);
        Assert.Equal(speedUntil, bot.SwarmBareSpeedUntilUtc);
    }

    private sealed class FixedTargetBehavior(Bot target)
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Selections { get; private set; }
        private readonly AreaType _area = target.Movement.DestinationArea;
        private readonly Cell? _destination = target.Movement.DestinationCell;
        public override void SelectMovementTarget(MatchRuntime runtime, Bot bot)
        {
            Selections++;
            bot.Movement.DestinationArea = _area;
            bot.Movement.DestinationCell = _destination;
        }
    }
}
