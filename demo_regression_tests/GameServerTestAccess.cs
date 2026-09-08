using game_server.matches;
using System.Reflection;
using game_server;
using game_server.services;
using game_server.sessions;

namespace demo_regression_tests;

// 서버의 private 구성 요소를 조립·수명 검증에서만 조회한다.
// 테스트 때문에 운영 코드의 접근 범위를 넓히지 않는다.
internal static class GameServerTestAccess
{
    internal static MatchRuntimeStore GetMatchRuntimes(this GameServer server) =>
        Read<MatchRuntimeStore>(server);

    internal static MatchArenaService GetArena(this GameServer server) => Read<MatchArenaService>(server);
    internal static MatchGrowthService GetGrowth(this GameServer server) => Read<MatchGrowthService>(server);

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
        var sessions = new game_server.sessions.GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.sessions.GameSessionRegistry>.Instance);
        var lifecycle = new MatchingLifecycleService(new InMemoryRedisOperations(),
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        var archive = new MatchEventArchive();
        runtimes ??= new MatchRuntimeStore(logger,
            cleanupSteps:
            [
                new("session runtime", MatchStartGate.RemoveMatching)
            ],
            afterCleanup: id => lifecycle.PrepareRedisCleanup(id).Invoke(),
            eventArchive: archive);
        var logs = new GameEventLogManager(id => runtimes.Get(id)?.EventLog, archive);
        var summaries = new MatchSummaryFileStore();
        var entryFailure = new MatchEntryFailureHandler(runtimes, sessions, lifecycle, logger);
        var orbUpgrades = new OrbUpgradeService(runtimes, logs, GameServerDevOptions.Disabled,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrbUpgradeService>.Instance);
        var orbTrails = new OrbTrailService(runtimes);
        var combatDamage = new MatchCombatDamageService(runtimes, logs, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchCombatDamageService>.Instance);
        var cleanup = new MatchCleanupService(runtimes, logs, summaries, logger);
        var matchEliminations = TestGameSessionServices.CreateEliminationService(
            runtimes, logs, summaries, GameServerDevOptions.Disabled, logger);
        var eliminations = new BotEliminationService(logs, matchEliminations, logger);
        var growth = new MatchGrowthService(runtimes, logs, orbUpgrades,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchGrowthService>.Instance);
        var field = new MatchFieldService(runtimes, logs, orbTrails,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchFieldService>.Instance);
        var movement = new BotMovementService( logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotMovementService>.Instance);
        var decisions = new BotDecisionService(runtimes, logs, growth, orbTrails,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotDecisionService>.Instance);
        var arena = new MatchArenaService(runtimes, GameServerDevOptions.Disabled, logs, cleanup,
            eliminations, matchEliminations, orbUpgrades, growth,
            new OrbRecoveryService(runtimes, logs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrbRecoveryService>.Instance),
            new OrbVisualStatePublisher(runtimes), orbTrails, combatDamage,
            new WindBladeService(runtimes, orbTrails, combatDamage, logs),
            new CrossfireService(runtimes, combatDamage, logs), field, movement, decisions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchArenaService>.Instance);
        return new GameServer(
            configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<GameServer>.Instance,
            sessionLogger: Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.sessions.GameClientSession>.Instance,
            matchingLifecycle: lifecycle,
            redisOperations: null!, networkService: null!,
            readinessState: new network.hosting.ServerReadinessState(),
            gameServerRegistry: new RecordingGameServerRegistry(),
            nodeOptions: new GameServerNodeOptions
            {
                NodeId = "game-server-test",
                PublicHost = "127.0.0.1"
            },
            devOptions: GameServerDevOptions.Disabled,
            sessions: sessions, matchRuntimes: runtimes, eventLogs: logs, matchEliminations: matchEliminations,
            matchEntry: TestGameSessionServices.CreateEntryService(new InMemoryRedisOperations(), runtimes, GameServerDevOptions.Disabled, logger),
            groundItemAutoPickup: new GroundItemAutoPickupService(logs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GroundItemAutoPickupService>.Instance),
            movementValidation: new MovementValidationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<MovementValidationService>.Instance),
            orbInventory: new OrbInventoryService(logs),
            entryFailureHandler: entryFailure,
            sessionLeaveHandler: new GameSessionLeaveHandler(sessions, cleanup,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GameSessionLeaveHandler>.Instance),
            countdown: new MatchCountdownService(runtimes, entryFailure, logger),
            fieldService: field,
            growth: growth,
            arena: arena,
            botDecisions: decisions,
            environmentService: new MatchEnvironmentService( logs,
                cleanup, eliminations, matchEliminations, GameServerDevOptions.Disabled,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchEnvironmentService>.Instance),
            tickService: new GameServerTickService(runtimes, Microsoft.Extensions.Logging.Abstractions.NullLogger<GameServerTickService>.Instance),
            botMovement: movement);
    }
}
