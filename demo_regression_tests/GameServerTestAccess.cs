using System.Reflection;
using game_server;
using game_server.services;

namespace demo_regression_tests;

// 서버의 private 구성 요소를 조립·수명 검증에서만 조회한다.
// 테스트 때문에 운영 코드의 접근 범위를 넓히지 않는다.
internal static class GameServerTestAccess
{
    internal static MatchRuntimeStore GetMatchRuntimes(this GameServer server) =>
        Read<MatchRuntimeStore>(server);

    internal static OrbUpgradeService GetOrbUpgrades(this GameServer server) => Read<OrbUpgradeService>(server);

    internal static GameEventLogManager GetEventLogs(this GameServer server) =>
        Read<GameEventLogManager>(server);

    internal static MatchEntryFailureHandler GetEntryFailureHandler(this GameServer server) =>
        Read<MatchEntryFailureHandler>(server);

    internal static MatchingLifecycleService GetMatchingLifecycle(this GameServer server) =>
        Read<MatchingLifecycleService>(server);

    private static T Read<T>(GameServer server) where T : class =>
        Assert.IsType<T>(typeof(GameServer)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(T))
            .GetValue(server));

    internal static GameServer Create(MatchRuntimeStore? runtimes = null)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var sessions = new game_server.network.GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.network.GameSessionRegistry>.Instance);
        var lifecycle = new MatchingLifecycleService(new InMemoryRedisOperations(),
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        var archive = new MatchEventArchive();
        runtimes ??= new MatchRuntimeStore(logger,
            cleanupSteps:
            [
                new("session runtime", game_server.network.GameClientSession.CleanupAbandonedMatchingRuntime),
                new("session index", sessions.RemoveMatch)
            ],
            afterCleanup: id => lifecycle.PrepareRedisCleanup(id).Invoke(),
            eventArchive: archive);
        var logs = new GameEventLogManager(id => runtimes.Get(id)?.EventLog, archive);
        var summaries = new MatchSummaryFileStore();
        var entryFailure = new MatchEntryFailureHandler(runtimes, sessions, lifecycle, logger);
        return new GameServer(
            configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<GameServer>.Instance,
            sessionLogger: Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.network.GameClientSession>.Instance,
            matchingLifecycle: lifecycle,
            redisOperations: null!, networkService: null!, gameHandoffTicketService: null!,
            readinessState: new network.hosting.ServerReadinessState(),
            gameServerRegistry: new RecordingGameServerRegistry(),
            nodeOptions: new GameServerNodeOptions
            {
                NodeId = "game-server-test", PublicHost = "127.0.0.1"
            },
            devOptions: GameServerDevOptions.Disabled,
            sessions: sessions, matchRuntimes: runtimes, eventLogs: logs, summaryFileStore: summaries,
            entryFailureHandler: entryFailure,
            sessionLeaveHandler: new GameSessionLeaveHandler(sessions,
                new MatchCleanupService(runtimes, sessions, logs, summaries, logger),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GameSessionLeaveHandler>.Instance),
            matchCleanup: new MatchCleanupService(runtimes, sessions, logs, summaries, logger),
            botEliminations: new BotEliminationService(sessions, logs, logger),
            countdown: new MatchCountdownService(runtimes, entryFailure, logger),
            orbUpgrades: new OrbUpgradeService(runtimes, logs, GameServerDevOptions.Disabled,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrbUpgradeService>.Instance),
            environmentService: new MatchEnvironmentService(sessions, logs,
                new MatchCleanupService(runtimes, sessions, logs, summaries, logger),
                new BotEliminationService(sessions, logs, logger), GameServerDevOptions.Disabled,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchEnvironmentService>.Instance),
tickService: new GameServerTickService(runtimes, Microsoft.Extensions.Logging.Abstractions.NullLogger<GameServerTickService>.Instance),
            botMovement: new BotMovementService(sessions, logs, Microsoft.Extensions.Logging.Abstractions.NullLogger<BotMovementService>.Instance));
    }
}
