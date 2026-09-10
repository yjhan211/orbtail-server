using game_server.players;
using game_server.orbs;
using game_server;
using game_server.items;
using game_server.logging;
using game_server.combat;
using game_server.matches.entry;
using game_server.matches;
using game_server.matches.results;
using network.common.data.models;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.gameentry;
using network.infrastructure.redis;

namespace demo_regression_tests;

internal static class TestGameSessionServices
{
    public static PlayerHealthService CreateHealthService(MatchRuntimeStore store, GameEventLogManager logs,
        MatchSummaryFileStore? summaries = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(logs, CreateEliminationService(store, logs, summaries ?? new MatchSummaryFileStore(), logger ?? NullLogger.Instance),
            NullLogger<PlayerHealthService>.Instance);

    public static OrbUpgradeService CreateOrbUpgradeService(MatchRuntimeStore store, GameEventLogManager logs) =>
        new(store, logs, NullLogger<OrbUpgradeService>.Instance);
    internal static void StartGameplay(this MatchRuntime runtime)
    {
        typeof(MatchRuntime).GetField("_startsAtUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(runtime, DateTime.UtcNow);
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
    public static GameClientSession CreateRecipientSession()
    {
        var logs = TestGameEventLogs.Create();
        var store = CreateMatchRuntimeStore(NullLogger.Instance);
        return new GameClientSession(
            new network.core.TcpConnection(), NullLogger.Instance, new InMemoryRedisOperations(),
            static _ => false, CreateMatchCleanupService(), static (_, _) => null,
            logs,
            CreateOrbUpgradeService(store, logs), new FakeGameSessionLifecycle(), static () => false,
            new FakeMatchEntryFailureHandler(),
            matchEntry: CreateEntryService(null, store, NullLogger.Instance),
            movementValidation: new MovementValidationService(NullLogger<MovementValidationService>.Instance),
            orbInventory: new OrbInventoryService(logs));
    }

    // 실제 Loop의 첫 처리 단계만 바꿔 틱 지연·예외·종료를 재현한다.
    public static Func<MatchRuntime, TimeProvider, MatchTickLoop> CreateTickLoopFactory(MatchRuntimeStore store, Action<MatchRuntime> processTick)
    {
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        return (runtime, clock) => TestMatchTickServices.CreateLoop(runtime, store, NullLogger<MatchTickLoop>.Instance,
            new GroundItemAutoPickupService(logs, CreateHealthService(store, logs), NullLogger<GroundItemAutoPickupService>.Instance),
            (matchingId, _) => processTick(store.GetOrThrow(matchingId)),
            (_, _) => { }, _ => { }, (_, _) => { }, clock);
    }

    // 단위 테스트도 실제 Lifecycle을 사용한다. Redis/NATS만 인메모리 구현으로 대체한다.
    public static MatchRuntimeStore CreateMatchRuntimeStore(
        Microsoft.Extensions.Logging.ILogger logger,
        Action<long>? onRedisCleanup = null)
    {
        var redis = new InMemoryRedisOperations();
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
        return new MatchRuntimeStore(logger.For<MatchRuntime>(), lifecycle, logger.For<MatchCombatDamageService>());
    }
    public static PlayerMovementService GetMovement(GameClientSession session) =>
        (PlayerMovementService)typeof(GameClientSession).GetField("PlayerMovement",
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
            matchingId > 0 ? (store?.GetOrCreate(matchingId) ?? entry!.GetOrCreateMatch(matchingId)) : null);
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
            new GameServerNodeOptions { NodeId = "game-server-test", PublicHost = "127.0.0.1" },
            new GameEventLogManager(id => store.GetOrNull(id)?.EventLog));
    }

    public static PlayerEliminationService CreateEliminationService(
        MatchRuntimeStore store,
        GameEventLogManager logs,
        MatchSummaryFileStore summaries,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var results = new MatchResultService(store, logs, summaries, logger);
        return new PlayerEliminationService(store, logs, results, logger);
    }
    public static MatchCleanupService CreateMatchCleanupService()
    {
        var store = CreateMatchRuntimeStore(NullLogger.Instance);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var cleanup = new MatchCleanupService(store, logs,
            new MatchSummaryFileStore(), NullLogger.Instance);
        return cleanup;
    }
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
