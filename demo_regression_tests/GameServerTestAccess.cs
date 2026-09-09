using game_server.bots;
using game_server.items;
using game_server.logging;
using game_server.orbs;
using game_server.players;
using game_server.combat;
using game_server.matches.entry;
using game_server.field;
using game_server.matches.lifecycle;
using game_server.matches.results;
using game_server.matches;
using System.Reflection;
using game_server;
using game_server.sessions;

namespace demo_regression_tests;

// 서버의 private 구성 요소를 조립·수명 검증에서만 조회한다.
// 테스트 때문에 운영 코드의 접근 범위를 넓히지 않는다.
internal static class GameServerTestAccess
{
    internal static MatchRuntimeStore GetMatchRuntimes(this GameServer server) =>
        Read<MatchRuntimeStore>(server);

    internal static MatchCombatService GetCombat(this GameServer server)
    {
        var ticks = Read<MatchTickService>(server);
        var factory = Read<Func<MatchRuntime, TimeProvider, MatchTickLoop>>(ticks);
        return Read<MatchCombatService>(factory.Target!);
    }
    internal static MatchGrowthService GetGrowth(this GameServer server) => Read<MatchGrowthService>(server);

    internal static GameEventLogManager GetEventLogs(this GameServer server) =>
        Read<GameEventLogManager>(server);

    internal static MatchEntryFailureHandler GetEntryFailureHandler(this GameServer server) =>
        Read<MatchEntryFailureHandler>(server);

    internal static MatchingLifecycleService GetMatchingLifecycle(this GameServer server) =>
        Read<MatchingLifecycleService>(server);

    private static T Read<T>(object instance) where T : class =>
        Assert.IsType<T>(instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(field => field.FieldType == typeof(T))
            .GetValue(instance));

    internal static GameServer Create(MatchRuntimeStore? runtimes = null)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var sessions = new game_server.sessions.GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.sessions.GameSessionRegistry>.Instance);
        var lifecycle = new MatchingLifecycleService(new InMemoryRedisOperations(),
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        runtimes ??= new MatchRuntimeStore(logger.For<MatchRuntime>(),

            matchingLifecycle: lifecycle, damageLogger: logger.For<MatchCombatDamageService>());
        var logs = runtimes.EventLogs;
        var summaries = new MatchSummaryFileStore();
        var entryFailure = new MatchEntryFailureHandler(runtimes, sessions, lifecycle, logger);
        var orbUpgrades = new OrbUpgradeService(runtimes, logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrbUpgradeService>.Instance);
        var orbTrails = new OrbTrailService(runtimes);
        var cleanup = new MatchCleanupService(runtimes, logs, summaries, logger);
        var matchEliminations = TestGameSessionServices.CreateEliminationService(
            runtimes, logs, summaries, logger);
        var eliminations = new BotEliminationService(logs, matchEliminations, logger);
        var growth = new MatchGrowthService(runtimes, logs, orbUpgrades,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchGrowthService>.Instance);
        var field = new MatchZoneService(runtimes, logs, orbTrails,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchZoneService>.Instance);
        var movement = new BotMovementService( logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotMovementService>.Instance);
        var decisions = new BotDecisionService(runtimes, logs, growth, orbTrails,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotDecisionService>.Instance);
        var combat = new MatchCombatService(runtimes, logs, cleanup,
            eliminations, matchEliminations, orbUpgrades, growth,
            new OrbRecoveryService(runtimes, logs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrbRecoveryService>.Instance),
            new OrbVisualStatePublisher(runtimes), orbTrails,
            new WindOrbAttackService(runtimes, orbTrails, logs),
            new SunOrbAttackService(runtimes, logs), field, decisions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchCombatService>.Instance);
        var countdown = new MatchCountdownService(runtimes, entryFailure, logger);
        var environment = new MatchEnvironmentService(logs,
            cleanup, eliminations, matchEliminations,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchEnvironmentService>.Instance);
        var groundPickup = new GroundItemAutoPickupService(logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GroundItemAutoPickupService>.Instance);
        Func<MatchRuntime, TimeProvider, MatchTickLoop> createLoop = (runtime, clock) => new MatchTickLoop(runtime, runtimes, logger, groundPickup,
            countdown, combat, environment, movement, decisions, field, clock);
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
            sessions: sessions, matchRuntimes: runtimes, eventLogs: logs, matchEliminations: matchEliminations,
            matchEntry: TestGameSessionServices.CreateEntryService(new InMemoryRedisOperations(), runtimes, logger),
            movementValidation: new MovementValidationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<MovementValidationService>.Instance),
            orbInventory: new OrbInventoryService(logs),
            entryFailureHandler: entryFailure,
            matchCleanup: cleanup,
            growth: growth,
            tickService: new MatchTickService(runtimes, createLoop, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchTickService>.Instance));
    }
}
