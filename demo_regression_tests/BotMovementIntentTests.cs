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
        bot.SetMovementTarget(bot.Player.CurrentArea, cell);
        var behavior = new FixedTargetBehavior(bot);
        var movement = new MatchMovementService(behavior, null!, null!);

        behavior.PrepareMovement(runtime, bot, now, false);
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
        var behavior = new FixedTargetBehavior(bot);
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
    public void PreparationReplansOnlyOnItsTurnAndMakesMovementReady(bool canPlanThisTick)
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
        bot.SetMovementTarget(bot.Player.CurrentArea, destination!);
        bot.LoopWaitUntil = DateTime.MinValue;
        var before = bot.Player.Position;
        var behavior = new FixedTargetBehavior(bot);

        behavior.PrepareMovement(runtime, bot, DateTime.UtcNow, canPlanThisTick);

        Assert.Equal(MovementPathRequest.None, bot.Movement.PathRequest);
        Assert.Same(before, bot.Player.Position);
        Assert.True(bot.Movement.Speed > 0f);
        Assert.True(bot.Movement.FollowPath);
        if (canPlanThisTick)
        {
            Assert.DoesNotContain(oldTarget, bot.Movement.Waypoints);
            Assert.NotEmpty(bot.Movement.Waypoints);
        }
        else
        {
            Assert.Same(oldTarget, Assert.Single(bot.Movement.Waypoints));
        }
        bot.Movement.ResetIntent();
        Assert.Equal(MovementPathRequest.None, bot.Movement.PathRequest);
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
        var target = new Vector3f(bot.Player.Position.X + 1f, bot.Player.Position.Y, 0f);
        bot.Movement.Waypoints.Add(target);
        bot.Movement.Speed = 3f;
        bot.Movement.FollowPath = true;
        bot.Movement.DodgeDirection = new Vector3f(1f, 0f, 0f);
        var behavior = new FixedTargetBehavior(bot);

        behavior.PrepareMovement(runtime, bot, DateTime.UtcNow, true);

        Assert.Equal(0, behavior.Selections);
        Assert.Same(target, Assert.Single(bot.Movement.Waypoints));
        Assert.Equal(0f, bot.Movement.Speed);
        Assert.False(bot.Movement.FollowPath);
        Assert.Null(bot.Movement.DodgeDirection);
    }

    [Theory]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void AvoidanceTransitionKeepsReplanTiming(bool planningTurn, bool underFire,
        bool previouslyAvoiding, bool avoidingNow, bool extendsDeadline)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987655);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var now = DateTime.UtcNow;
        var deadline = now.AddSeconds(1);
        bot.SetMovementTarget(bot.Player.CurrentArea, cell);
        bot.Movement.NextPathPlanAtUtc = deadline;
        bot.WasAvoidingMonsterAtLastPathPlan = previouslyAvoiding;
        bot.LastDamagedAtUtc = underFire ? now : now.AddMinutes(-1);
        bot.MonsterAvoidanceTarget = avoidingNow ? (bot.Player.Position!, now) : null;

        new FixedTargetBehavior(bot).PrepareMovement(runtime, bot, now, planningTurn);

        Assert.Equal(extendsDeadline ? now.AddSeconds(1.5) : deadline, bot.Movement.NextPathPlanAtUtc);
        Assert.Equal(planningTurn && underFire ? avoidingNow : previouslyAvoiding,
            bot.WasAvoidingMonsterAtLastPathPlan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DodgeWithoutNewAdviceKeepsDirectionOnlyUntilHoldExpires(bool holding)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987656);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var bot = runtime.Bots.GetBot(-1)!;
        var before = bot.Player.Position!;
        var now = DateTime.UtcNow;
        bot.SetMovementTarget(bot.Player.CurrentArea, cell);
        bot.Movement.Waypoints.Add(new Vector3f(before.X + 0.1f, before.Y, 0f));
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.SwarmDodgeDirectionX = 1f;
        bot.SwarmDodgeDirectionY = 0f;
        bot.SwarmDodgeHoldUntilUtc = holding ? now.AddSeconds(1) : now;

        new FixedTargetBehavior(bot).PrepareMovement(runtime, bot, now, false);

        Assert.True(bot.Movement.Speed > 0f);
        Assert.True(bot.Movement.FollowPath);
        if (holding)
        {
            Assert.NotNull(bot.Movement.DodgeDirection);
            Assert.Equal(1f, bot.Movement.DodgeDirection!.X);
            Assert.Equal(0f, bot.Movement.DodgeDirection.Y);
        }
        else
        {
            Assert.Null(bot.Movement.DodgeDirection);
        }
        var result = MatchMovementService.Move(runtime, bot.Player.GameInfo.ObjectInfo, bot.Movement, 0.01f);
        Assert.True(result.Changed);
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
        bot.Movement.Destination = null;
        new FixedTargetBehavior(bot).PrepareMovement(runtime, bot, DateTime.UtcNow, false);
        Assert.Equal(speedUntil, bot.SwarmBareSpeedUntilUtc);
    }

    private sealed class FixedTargetBehavior(Bot target)
        : BotBehaviorService(null!, null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Selections { get; private set; }
        private readonly AreaType _area = target.Movement.DestinationArea;
        private readonly Vector3f? _destination = target.Movement.Destination;
        public override void SelectMovementTarget(MatchRuntime runtime, Bot bot)
        {
            Selections++;
            bot.Movement.DestinationArea = _area;
            bot.Movement.Destination = _destination;
        }
    }
}
