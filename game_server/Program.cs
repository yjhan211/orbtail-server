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
using game_server.sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.gameentry;
using network.hosting;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using network.routing;
using Serilog;
using Serilog.Events;

namespace game_server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .UseSerilog(ConfigureSerilog)
            .ConfigureServices(ConfigureServices)
            .RunConsoleAsync();
    }

    private static void ConfigureSerilog(HostBuilderContext hostingContext, LoggerConfiguration loggerConfiguration)
    {
        const string serverType = "GameServer";
        string nodeId = hostingContext.Configuration["gameServerId"] ?? "unconfigured";
        loggerConfiguration
            .MinimumLevel.Is(ResolveMinimumLevel(hostingContext.Configuration))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("nodeId", nodeId)
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [NodeId:{nodeId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
    }

    private static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        return Enum.TryParse(configuration["logLevel"], true, out LogEventLevel level)
            ? level
            : LogEventLevel.Debug;
    }

    private static GameServerNodeOptions CreateGameServerNodeOptions(IConfiguration configuration)
    {
        return new GameServerNodeOptions
        {
            NodeId = configuration["gameServerId"] ?? "",
            PublicHost = configuration["GAME_SERVER_PUBLIC_HOST"] ?? "",
            PublicPort = configuration.GetValue("GAME_SERVER_PUBLIC_PORT", GameServerNodeOptions.DefaultPublicPort),
            MaxConcurrentMatches = configuration.GetValue(
                "GAME_SERVER_MAX_MATCHES",
                GameServerNodeOptions.DefaultMaxConcurrentMatches)
        };
    }

    internal static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        var nodeOptions = CreateGameServerNodeOptions(hostContext.Configuration);
        nodeOptions.Validate();
        services.AddSingleton(nodeOptions);

        services.AddSingleton<NetworkService>();
        string natsEndpoint = hostContext.Configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("natsEndPoint is not configured.");
        services.AddSingleton<NatsClientFactory>(sp =>
            new NatsClientFactory(natsEndpoint, sp.GetRequiredService<ILogger<NatsClient>>()));
        services.AddSingleton<INatsClient>(sp => sp.GetRequiredService<NatsClientFactory>().Create());

        var redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton(_ => new RedisConnection(redisConfiguration));
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddGameEntryTicket(hostContext.Configuration);


        services.AddSingleton<IGameServerRegistry, RedisGameServerRegistry>();
        services.AddSingleton<GameSessionRegistry>();
        services.AddSingleton<MatchingLifecycleService>(sp => new MatchingLifecycleService(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<INatsClient>(),
            sp.GetRequiredService<ILogger<MatchingLifecycleService>>()));
        services.AddSingleton<MatchRuntimeStore>(sp =>
        {
            var lifecycle = sp.GetRequiredService<MatchingLifecycleService>();
            return new MatchRuntimeStore(
                sp.GetRequiredService<ILogger<MatchRuntime>>(),

                matchingLifecycle: lifecycle,
                damageLogger: sp.GetRequiredService<ILogger<MatchCombatDamageService>>());
        });
        services.AddSingleton<GameEventLogManager>(sp =>
        {
            var runtimes = sp.GetRequiredService<MatchRuntimeStore>();
            return runtimes.EventLogs;
        });
        services.AddSingleton<MatchSummaryFileStore>(_ => new MatchSummaryFileStore(
            hostContext.Configuration["MATCH_SUMMARY_DIRECTORY"],
            hostContext.Configuration.GetValue("MATCH_SUMMARY_MAX_FILES", MatchSummaryFileStore.DefaultMaxSummaries)));
        services.AddSingleton<MatchEntryFailureHandler>(sp => new MatchEntryFailureHandler(
            sp.GetRequiredService<MatchRuntimeStore>(), sp.GetRequiredService<GameSessionRegistry>(),
            sp.GetRequiredService<MatchingLifecycleService>(), sp.GetRequiredService<ILogger<MatchEntryFailureHandler>>()));
        services.AddSingleton<MatchCleanupService>(sp => new MatchCleanupService(
            sp.GetRequiredService<MatchRuntimeStore>(),
            sp.GetRequiredService<GameEventLogManager>(), sp.GetRequiredService<MatchSummaryFileStore>(),
            sp.GetRequiredService<ILogger<MatchCleanupService>>()));
        services.AddSingleton<MovementValidationService>();
        services.AddSingleton<OrbInventoryService>();
        services.AddSingleton<GameMatchEntryService>(sp => new GameMatchEntryService(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<MatchRuntimeStore>(),
            sp.GetRequiredService<ILogger<GameMatchEntryService>>(),
            sp.GetRequiredService<GameEntryTicketService>(),
            sp.GetRequiredService<GameServerNodeOptions>(),
            sp.GetRequiredService<GameEventLogManager>()));
        services.AddSingleton<MatchResultService>(sp => new MatchResultService(
            sp.GetRequiredService<MatchRuntimeStore>(),
            sp.GetRequiredService<GameEventLogManager>(),
            sp.GetRequiredService<MatchSummaryFileStore>(),
            sp.GetRequiredService<ILogger<MatchResultService>>()));
        services.AddSingleton<MatchEliminationService>(sp => new MatchEliminationService(
            sp.GetRequiredService<MatchRuntimeStore>(),
            sp.GetRequiredService<GameEventLogManager>(),
            sp.GetRequiredService<MatchResultService>(),
            sp.GetRequiredService<GroundItemDropService>(),
            sp.GetRequiredService<ILogger<MatchEliminationService>>()));
        services.AddSingleton<BotEliminationService>(sp => new BotEliminationService(
            sp.GetRequiredService<GameEventLogManager>(),
            sp.GetRequiredService<MatchEliminationService>(),
            sp.GetRequiredService<ILogger<BotEliminationService>>()));
        services.AddSingleton<MatchEnvironmentService>();
        services.AddSingleton<OrbUpgradeService>();
        services.AddSingleton<MatchGrowthService>();
        services.AddSingleton<OrbRecoveryService>();
        services.AddSingleton<GroundItemAutoPickupService>();
        services.AddSingleton<GroundItemDropService>();
        services.AddSingleton<OrbVisualStatePublisher>();
        services.AddSingleton<OrbTrailService>();
        services.AddSingleton<WindOrbAttackService>();
        services.AddSingleton<SunOrbAttackService>();
        services.AddTransient<MatchZoneService>();
        services.AddSingleton<BotMovementService>();
        services.AddSingleton<BotDecisionService>();
        services.AddTransient<MatchCombatService>();
        // 생성 함수만 공유한다. 루프와 전투·자기장 서비스는 매치마다 만든다.
        services.AddSingleton<Func<MatchRuntime, TimeProvider, MatchTickLoop>>(sp =>
        {
            var environment = sp.GetRequiredService<MatchEnvironmentService>();
            var botMovement = sp.GetRequiredService<BotMovementService>();
            var botDecisions = sp.GetRequiredService<BotDecisionService>();
            var runtimes = sp.GetRequiredService<MatchRuntimeStore>();
            var loopLogger = sp.GetRequiredService<ILogger<MatchTickLoop>>();
            var pickup = sp.GetRequiredService<GroundItemAutoPickupService>();
            return (runtime, clock) =>
            {
                var entryFailureHandler = sp.GetRequiredService<MatchEntryFailureHandler>();
                var zones = sp.GetRequiredService<MatchZoneService>();
                var combat = ActivatorUtilities.CreateInstance<MatchCombatService>(sp, zones);
                return new MatchTickLoop(
                    runtime,
                    runtimes, loopLogger, pickup,
                    entryFailureHandler, combat, environment, botMovement, botDecisions, zones, clock);
            };
        });
        services.AddSingleton<MatchTickService>();
        services.AddSingleton<GameServer>();

        services.AddSingleton<ServerReadinessState>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameServer>());
    }
}
