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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TickSuppliesBeforeMovementExceptForMapValidation(bool started, bool solo)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(987636);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.InitializeMatch(solo ? MatchMode.SoloMapValidation : MatchMode.Normal,
            new Dictionary<long, Cell> { [1] = cell }, [new PlayerInfo { PlayerId = 1 }]);
        var player = runtime.GetParticipant(1)!;
        player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        player.CurrentArea = AreaType.S2Corridor9;
        if (started) runtime.StartGameplay();
        bool moved = false;
        var loop = TestMatchTickServices.CreateLoop(runtime, store, NullLogger.Instance,
            new game_server.players.PlayerPickupService(TestGameSessionServices.CreateHealthService(store),
                NullLogger<game_server.players.PlayerPickupService>.Instance),
            (_, _) => { }, (_, _) => { }, match =>
            {
                Assert.Equal(solo ? 0 : 1, match.Monsters.MaxParticipantCount);
                moved = true;
            }, _ => { });
        try
        {
            loop.ProcessTick();
            Assert.True(moved);
        }
        finally
        {
            loop.Stop();
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickRunsMovementOnlyAfterStart(bool started)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987631);
        var bots = new BotProbe();
        var monsters = new MonsterBehaviorService();
        var movement = new MatchMoveService(bots, monsters);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        // 봇의 Player도 운영과 동일하게 참가자 목록에 등록한다.
        runtime.RegisterParticipant(runtime.Bots.GetBot(-1)!.Player);
        var initializedAt = DateTime.UtcNow;
        runtime.Monsters.Initialize(initializedAt);
        if (started) runtime.StartGameplay();

        movement.ProcessTick(runtime, DateTime.UtcNow);

        Assert.Equal(started ? 1 : 0, bots.Calls);
        Assert.Equal(started ? 1 : 0, bots.Decisions);
        Assert.True(runtime.Monsters.IsInitialized);
        Assert.Equal(initializedAt, runtime.Monsters.StartsAtUtc);
    }

    [Fact]
    public void CountdownSkipsMonsterMovementAndFirstTickExcludesWaitingTime()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987638);
        using var scope = runtime.Enter();
        var start = DateTime.UtcNow;
        runtime.StartGameplay(start);
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var monster = new Monster
        {
            MonsterId = 1, Alive = true, Position = position,
            Area = AreaType.S2Corridor9, Health = 10
        };
        monster.Movement.LastProcessedAtUtc = start.AddSeconds(-10);
        monster.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(position.X + 1f, position.Y, 0f)));
        runtime.Monsters.Entities[1] = monster;
        var service = new MatchMoveService(null!, new MonsterBehaviorService());

        service.ProcessTick(runtime, start.AddMilliseconds(-50));
        Assert.Equal(position, monster.Position);
        Assert.Equal(start.AddSeconds(-10), monster.Movement.LastProcessedAtUtc);

        service.ProcessTick(runtime, start);
        Assert.Equal(position, monster.Position);
        Assert.Equal(start, monster.Movement.LastProcessedAtUtc);

        service.ProcessTick(runtime, start.AddMilliseconds(50));
        Assert.True(monster.Position.X > position.X);
    }
    [Fact]
    public void TickRequiresLockAndDoesNotRunAfterEnd()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987632);
        var movement = new MatchMoveService(null!, null!);
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
    [InlineData(250)]
    [InlineData(1000)]
    public void BotMovementClampsElapsedTime(int elapsedMilliseconds)
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
        runtime.StartGameplay(now.AddSeconds(-10));
        bot.Movement.LastProcessedAtUtc = now.AddMilliseconds(-elapsedMilliseconds);
        // 이동 시간 상한만 검증하도록 기존 경로의 재계획을 미룬다.
        bot.Movement.NextPathPlanAtUtc = now.AddSeconds(1);
        bot.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(before.X + 1f, before.Y, 0f)));
        var service = new MatchMoveService(new MovingBotProbe(), null!);

        service.ProcessTick(runtime, now);

        Assert.Equal(elapsedMilliseconds > 0, !before.Equals(bot.Player.Position));
        float expectedDistance = Math.Clamp(elapsedMilliseconds / 1000f, 0f, 0.25f);
        Assert.Equal(expectedDistance, bot.Player.Position!.X - before.X, 4);
        Assert.Equal(now, bot.Movement.LastProcessedAtUtc);
        Assert.True(float.IsFinite(bot.Player.Velocity.X));
    }

    [Fact]
    public void RegisteredBotHasPositionAndUpdatesMovementTimestamp()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987634);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1L], new Dictionary<long, Cell> { [-1] = cell });
        runtime.StartGameplay();
        var bot = runtime.Bots.GetBot(-1)!;
        Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell), bot.Player.Position);
        var behavior = new BotProbe();
        var service = new MatchMoveService(behavior, null!);

        var now = DateTime.UtcNow;
        service.ProcessTick(runtime, now);

        Assert.Equal(1, behavior.Calls);
        Assert.NotNull(bot.Player.Position);
        Assert.Equal(now, bot.Movement.LastProcessedAtUtc);
    }

    [Fact]
    public void NewMonsterCanRequestMovementImmediately()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987635);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        runtime.RegisterParticipant(new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 1 }, Health = 100,
            Position = position, CurrentArea = AreaType.S2Corridor9
        });
        var monster = new Monster
        {
            MonsterId = 1, Position = position, Area = AreaType.S2Corridor9,
            Alive = true, Health = 100
        };
        var behavior = new MonsterBehaviorService();
        var request = behavior.CreateMovementRequest(runtime, monster, [], now);

        Assert.False(request.HoldPosition);
        Assert.True(request.Speed > 0f);
    }

    private sealed class MovingBotProbe()
        : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public override MovementRequest CreateMovementRequest(MatchRuntime runtime, Bot bot, DateTime now)
        {
            return new MovementRequest(null, 1f);
        }
    }

    private sealed class BotProbe() : BotBehaviorService(null!, null!, NullLogger<BotBehaviorService>.Instance)
    {
        public int Calls { get; private set; }
        public int Decisions { get; private set; }
        public override Cell? SelectMovementTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
        {
            Assert.True(Monitor.IsEntered(runtime.MatchLock));
            Calls++;
            Decisions++;
            return null;
        }
    }
}
