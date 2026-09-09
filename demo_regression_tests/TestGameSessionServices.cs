using game_server.matches.entry;
using game_server.matches.lifecycle;
using game_server.matches.results;
using game_server.matches;
using game_server.network;
using network.common.data.models;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.gameentry;
using network.infrastructure.redis;

namespace demo_regression_tests;

internal sealed class FakePlayerGrowthHandler(
    Func<GameClientSession, long, int, long, long, (bool Success, int ResultItemId, int TargetOrdinal)>? orbDecision = null) : IPlayerGrowthHandler
{

    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(long matchingId, long playerId) => new();

    public (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid) =>
        orbDecision?.Invoke(session, matchingId, action, targetItemUid, secondItemUid) ?? (false, 0, -1);
}

internal static class TestGameSessionServices
{
    // 실제 Loop의 첫 처리 단계만 바꿔 틱 지연·예외·종료를 재현한다.
    public static Func<MatchRuntime, TimeProvider, MatchTickLoop> CreateTickLoopFactory(MatchRuntimeStore store, Action<MatchRuntime> processTick)
    {
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        return (runtime, clock) => TestMatchTickServices.CreateLoop(runtime, store, NullLogger<MatchTickLoop>.Instance,
            new GroundItemAutoPickupService(logs, NullLogger<GroundItemAutoPickupService>.Instance),
            (matchingIds, _) => processTick(store.GetOrThrow(matchingIds.Single())),
            (_, _) => { }, (_, _) => { }, _ => { }, (_, _) => { }, clock);
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
        var lifecycle = new MatchingLifecycleService(redis,
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        return new MatchRuntimeStore(logger.For<MatchRuntime>(), lifecycle);
    }
    public static PlayerMovementService GetMovement(GameClientSession session) =>
        (PlayerMovementService)typeof(GameClientSession).GetField("_playerMovement",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;

    public static void SetMovementProperty(GameClientSession session, string name, object? value) =>
        typeof(PlayerMovementService).GetProperty(name)!.SetValue(GetMovement(session), value);

    // 입장 프로토콜을 생략하는 단위 테스트에서도 실제 입장과 같은 런타임을 세션에 연결한다.
    public static void BindMatch(GameClientSession session, long matchingId, MatchRuntimeStore? store = null)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var entry = (GameMatchEntryService?)typeof(GameClientSession).GetField("_matchEntry", flags)!.GetValue(session);
        typeof(GameClientSession).GetField("_match", flags)!.SetValue(session,
            matchingId > 0 ? (store?.GetOrCreate(matchingId) ?? entry!.GetOrCreateMatch(matchingId)) : null);
    }

    public static GameMatchEntryService CreateEntryService(
        IRedisOperations? redis, MatchRuntimeStore store, GameServerDevOptions options,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        redis ??= new InMemoryRedisOperations();
        return new GameMatchEntryService(redis, store, options, logger,
            new GameEntryTicketService(new RedisGameEntryTicketStore(redis), new GameEntryTicketOptions()),
            new GameServerNodeOptions { NodeId = "game-server-test", PublicHost = "127.0.0.1" },
            new GameEventLogManager(id => store.GetOrNull(id)?.EventLog));
    }

    public static MatchEliminationService CreateEliminationService(
        MatchRuntimeStore store,
        GameEventLogManager logs,
        MatchSummaryFileStore summaries,
        GameServerDevOptions options,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var results = new MatchResultService(store, logs, summaries, options, logger);
        return new MatchEliminationService(store, logs, results, new GroundItemDropService(logs), options, logger);
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
    : IGameSessionLifecycle
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
