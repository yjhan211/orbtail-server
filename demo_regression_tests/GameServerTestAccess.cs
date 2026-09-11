using System.Reflection;
using game_server;
using game_server.matches;
using game_server.matches.combat;
using game_server.matches.entry;
using game_server.matches.logging;
using game_server.matches.results;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;

namespace demo_regression_tests;

// 서버의 private 구성 요소를 조립·수명 검증에서만 조회한다.
// 테스트 때문에 운영 코드의 접근 범위를 넓히지 않는다.
internal static class GameServerTestAccess
{
    internal static MatchRuntimeStore GetMatchRuntimes(this GameServer server) =>
        Read<MatchRuntimeStore>(server);

    internal static MatchCombatService GetCombat(this GameServer server, long matchingId) =>
        Read<MatchCombatService>(GetLoop(server, matchingId));

    internal static MatchTickLoop GetLoop(GameServer server, long matchingId)
    {
        var runtime = server.GetMatchRuntimes().GetOrThrow(matchingId);
        var ticks = Read<MatchTickService>(server);
        var factory = Read<Func<MatchRuntime, TimeProvider, MatchTickLoop>>(ticks);
        return runtime.TickLoop ??= factory(runtime, TimeProvider.System);
    }
    internal static PlayerOrbGrowthService GetOrbGrowth(this GameServer server) => Read<PlayerOrbGrowthService>(server);

    internal static GameEventLogManager GetEventLogs(this GameServer server) =>
        Read<GameEventLogManager>(server);

    internal static MatchEntryFailureHandler GetEntryFailureHandler(this GameServer server) =>
        Read<MatchEntryFailureHandler>(server);

    internal static MatchSessionCleanupService GetMatchingLifecycle(this GameServer server) =>
        Read<MatchSessionCleanupService>(server);

    private static T Read<T>(object instance) where T : class =>
        Assert.IsType<T>(instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(field => field.FieldType == typeof(T))
            .GetValue(instance));

    internal static GameServer Create(MatchRuntimeStore? runtimes = null)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var sessions = new game_server.sessions.GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.sessions.GameSessionRegistry>.Instance);
        var lifecycle = new MatchSessionCleanupService(new InMemoryRedisOperations(),
            new MatchStartCountdownPublicationTests.NoOpNatsClient(), logger);
        runtimes ??= new MatchRuntimeStore(logger.For<MatchRuntime>(), matchSessionCleanup: lifecycle);
        var logs = runtimes.EventLogs;
        var summaries = new MatchSummaryFileStore();
        var entryFailure = new MatchEntryFailureHandler(runtimes, sessions, lifecycle, logger);
        var growth = new PlayerOrbGrowthService(logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerOrbGrowthService>.Instance);
        var orbTrails = new PlayerOrbTrailService();
        var cleanup = new MatchCleanupService(runtimes, logs, summaries, logger);
        var matchEliminations = TestGameSessionServices.CreateEliminationService(
            runtimes, logs, summaries, logger);
        var health = TestGameSessionServices.CreateHealthService(runtimes, logs, summaries, logger);
        var combatDamage = TestGameSessionServices.CreateCombatDamageService(logs);
        var results = new MatchResultService(runtimes, logs, summaries, logger);
        var movement = new BotMovementService(logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotMovementService>.Instance);
        var interactions = new PlayerInteractionService();
        var decisions = new BotDecisionService(logs, growth, orbTrails, interactions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotDecisionService>.Instance);
        var field = new MatchFieldService(logs, orbTrails, health,
            cleanup, matchEliminations, results);
        var groundPickup = new PlayerPickupService(logs, health,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerPickupService>.Instance);
        Func<MatchRuntime, TimeProvider, MatchTickLoop> createLoop = (runtime, clock) =>
        {
            var combat = new MatchCombatService(logs, cleanup,
                health, combatDamage, results,
                new PlayerOrbService(health, combatDamage, orbTrails, logs),
                new OrbVisualStatePublisher(), orbTrails,
                new SunOrbAttackService(TestGameSessionServices.CreateHealthService(runtimes, logs, new game_server.matches.results.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), combatDamage, logs),
                new WaveOrbAttackService(health, combatDamage, logs), decisions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchCombatService>.Instance);

            return new MatchTickLoop(runtime, runtimes, logger, groundPickup,
            entryFailure, combat, field, movement, decisions, clock);
        };
        return new GameServer(
            configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<GameServer>.Instance,
            sessionLogger: Microsoft.Extensions.Logging.Abstractions.NullLogger<game_server.sessions.GameClientSession>.Instance,
            matchSessionCleanup: lifecycle,
            redisOperations: null!, networkService: null!,
            readinessState: new network.hosting.ServerReadinessState(),
            gameServerRegistry: new RecordingGameServerRegistry(),
            nodeOptions: new GameServerNodeOptions
            {
                NodeId = "game-server-test",
                PublicHost = "127.0.0.1"
            },
            sessions: sessions, matchRuntimes: runtimes, eventLogs: logs,
            matchEntry: TestGameSessionServices.CreateEntryService(new InMemoryRedisOperations(), runtimes, logger),
            entryFailureHandler: entryFailure,
            matchCleanup: cleanup,
            orbGrowth: growth,
            movement: new PlayerMovementService(logs, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerMovementService>.Instance),
            interactions: interactions,
            tickService: new MatchTickService(runtimes, createLoop, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchTickService>.Instance));
    }
}
