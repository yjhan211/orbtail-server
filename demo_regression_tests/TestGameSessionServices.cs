using game_server;
using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data.models;
using network.gameentry;
using network.infrastructure.redis;

namespace demo_regression_tests;

internal static class TestGameSessionServices
{
    // 입장 절차가 관심사가 아닌 단위 테스트용 빈 매치 등록.
    internal static MatchRuntime GetOrCreate(this MatchRuntimeStore store, long matchingId) =>
        store.GetOrNull(matchingId) ?? store.Register(store.Create(matchingId));

    // 몬스터 단계는 운영 조율자를 그대로 실행하고 다른 전투 단계 의존성은 사용하지 않는다.
    internal static MatchCombatService CreateMonsterTickService()
    {
        var movement = new MonsterBehaviorService();
        var spawns = new MatchMonsterSpawnService(movement);
        return new MatchCombatService(null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, new MonsterCombatService(spawns));
    }

    public static PlayerHealthService CreateHealthService(MatchRuntimeStore store, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(CreateEliminationService(store, logger ?? NullLogger.Instance), NullLogger<PlayerHealthService>.Instance);

    public static PlayerOrbGrowthService CreatePlayerOrbGrowthService() =>
        new(NullLogger<PlayerOrbGrowthService>.Instance);
    internal static void StartGameplay(this MatchRuntime runtime, DateTime? startsAtUtc = null)
    {
        typeof(MatchRuntime).GetField("_startsAtUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(runtime, startsAtUtc ?? DateTime.UtcNow);
    }

    internal static void PrepareEntry(this MatchRuntime runtime, params long[] playerIds)
    {
        using (runtime.Enter())
        {
            if (!runtime.IsSetupComplete)
                runtime.InitializeMatch(MatchMode.Normal, new Dictionary<long, Cell>(),
                    playerIds.Select(id => new PlayerInfo { PlayerId = id }).ToList());
            runtime.BeginEntry(playerIds[0]);
        }
    }
    // 송신 계획의 수신자 참조를 검사하기 위한 소켓 없는 세션.
    public static GameClientSession CreateRecipientSession(network.core.TcpConnection? connection = null)
    {
        var store = CreateMatchRuntimeStore(NullLogger.Instance);
        return new GameClientSession(
            connection ?? new network.core.TcpConnection(), NullLogger.Instance, new InMemoryRedisOperations(),
            static _ => false, CreateMatchCleanupService(), static (_, _) => null,
            CreatePlayerOrbGrowthService(), CreateMovementService(), new PlayerInteractionService(), new FakeGameSessionLifecycle(), static () => false,
            new FakeMatchEntryFailureHandler(),
            matchEntry: CreateEntryService(null, store, NullLogger.Instance));
    }

    // 실제 Loop의 첫 처리 단계만 바꿔 틱 지연·예외·종료를 재현한다.
    public static Func<MatchRuntime, TimeProvider, MatchTickLoop> CreateTickLoopFactory(MatchRuntimeStore store, Action<MatchRuntime> processTick)
    {
        return (runtime, clock) => TestMatchTickServices.CreateLoop(runtime, store, NullLogger<MatchTickLoop>.Instance,
            new PlayerPickupService(CreateHealthService(store), NullLogger<PlayerPickupService>.Instance),
            (matchingId, _) => processTick(store.GetOrThrow(matchingId)),
            (_, _) => { }, _ => { }, _ => { }, clock);
    }

    // 단위 테스트도 실제 Lifecycle을 사용한다. Redis/NATS만 인메모리 구현으로 대체한다.
    public static MatchRuntimeStore CreateMatchRuntimeStore(
        Microsoft.Extensions.Logging.ILogger logger,
        Action<long>? onRedisCleanup = null, InMemoryRedisOperations? redis = null)
    {
        redis ??= new InMemoryRedisOperations();
        if (onRedisCleanup != null)
        {
            redis.BeforeKeyDeleteAsync = key =>
            {
                long matchingId = long.Parse(key.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                onRedisCleanup(matchingId);
                return Task.CompletedTask;
            };
        }
        var lifecycle = new MatchSessionCleanupService(redis,
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        return new MatchRuntimeStore(logger.For<MatchRuntime>(), lifecycle, redis);
    }
    public static MatchCombatDamageService CreateCombatDamageService() =>
        new(new MonsterCombatService(new MatchMonsterSpawnService(new MonsterBehaviorService())));

    public static PlayerMovementService CreateMovementService() =>
        new(NullLogger<PlayerMovementService>.Instance);

    public static PlayerMovementService GetMovement(GameClientSession session) =>
        (PlayerMovementService)typeof(GameClientSession).GetField("_movement",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;

    // 실제 입장처럼 참가자 등록을 마친 뒤 해당 플레이어에 연결을 붙인다.
    public static void AttachSession(GameClientSession session)
    {
        var match = session.Match;
        using (match.Enter())
        {
            if (match.GetParticipant(session.PlayerId!.Value) == null)
                match.RegisterParticipant(session.Player);
            session.Player = match.GetParticipant(session.PlayerId.Value)!;
            session.Player.Session = session;
        }
    }

    public static int GetPeriodicBuffCount(Player player) =>
        ((System.Collections.ICollection)typeof(Player)
            .GetField("_periodicBuffs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(player)!).Count;

    public static void SetMovementProperty(GameClientSession session, string name, object? value) =>
        typeof(Player).GetProperty(name)!.SetValue(session.Player, value);

    // 입장 프로토콜을 생략하는 단위 테스트에서도 실제 입장과 같은 런타임을 세션에 연결한다.
    public static void BindMatch(GameClientSession session, long matchingId, MatchRuntimeStore? store = null)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var entry = (GameMatchEntryService?)typeof(GameClientSession).GetField("_matchEntry", flags)!.GetValue(session);
        typeof(GameClientSession).GetField("_match", flags)!.SetValue(session,
            matchingId > 0 ? (store?.GetOrCreate(matchingId) ?? ((MatchRuntimeStore)typeof(GameMatchEntryService).GetField("<matchRuntimes>P", flags)!.GetValue(entry)!).GetOrCreate(matchingId)) : null);
        session.Player = (matchingId > 0 ? session.Match.GetParticipant(session.PlayerId ?? 0) : null)
            ?? new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = session.PlayerId ?? 0 } };
    }

    public static GameMatchEntryService CreateEntryService(
        IRedisOperations? redis, MatchRuntimeStore store,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        redis ??= new InMemoryRedisOperations();
        return new GameMatchEntryService(redis, store, logger,
            new GameEntryTicketService(new RedisGameEntryTicketStore(redis), new GameEntryTicketOptions()),
            new GameServerNodeOptions { NodeId = "game-server-test", PublicHost = "127.0.0.1" });
    }

    /// <summary>참가자·봇·미등록 순으로 플레이어를 찾고, 없으면 참가자로 등록한다.</summary>
    public static Player GetOrRegisterPlayer(MatchRuntime match, long playerId)
    {
        var player = match.GetParticipant(playerId) ?? match.Bots.GetBot(playerId)?.Player;
        if (player != null)
            return player;
        player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
        match.RegisterParticipant(player);
        return player;
    }

    /// <summary>참가자(없으면 등록)의 배낭.</summary>
    public static PlayerOrbCollection Orbs(MatchRuntime match, long playerId) => GetOrRegisterPlayer(match, playerId).Orbs;

    public static SummonStoneStateInfo SummonStones(MatchRuntime match, long playerId)
    {
        var player = match.GetParticipant(playerId) ?? match.Bots.GetBot(playerId)?.Player;
        return player == null ? SummonStoneStateInfo.Empty : player.SummonStones;
    }

    public static SummonStoneStateInfo AddSummonStones(MatchRuntime match, long playerId, int amount)
    {
        var player = GetOrRegisterPlayer(match, playerId);
        using (match.Enter())
            return PlayerOrbGrowthService.AddSummonStones(match, player, amount);
    }

    public static bool SpendSummonStones(MatchRuntime match, long playerId, int amount)
    {
        var player = GetOrRegisterPlayer(match, playerId);
        using (match.Enter())
            return PlayerOrbGrowthService.TrySpendSummonStones(match, player, amount);
    }

    public static PlayerEliminationService CreateEliminationService(
        MatchRuntimeStore store,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var results = new MatchResultService(store, logger);
        return new PlayerEliminationService(results, logger);
    }
    public static MatchCleanupService CreateMatchCleanupService() =>
        new(CreateMatchRuntimeStore(NullLogger.Instance), NullLogger.Instance);
}

internal sealed class FakeGameSessionLifecycle(Func<long, long, Action?>? prepareCompletion = null)
    : IMatchSessionCleanup
{
    public void PublishPlayerLeft(long playerId, long matchingId) { }
    public Action? PrepareGameCompletion(long playerId, long matchingId) =>
        prepareCompletion?.Invoke(playerId, matchingId);
    public void ReleaseMatchingReservation(long playerId, long matchingId) { }
}

internal sealed class FakeMatchEntryFailureHandler(Action<GameClientSession>? handle = null)
    : IMatchEntryFailureHandler
{
    public void Handle(GameClientSession session) => handle?.Invoke(session);
}
