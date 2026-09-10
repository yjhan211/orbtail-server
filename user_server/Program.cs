using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.gameentry;
using network.hosting;
using network.infrastructure;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using network.routing;
using Serilog;
using Serilog.Events;
using user_server.accounts;
using user_server.matching;
using user_server.matching.creation;
using user_server.matching.queue;
using user_server.players;
using user_server.sessions;

namespace user_server;

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
        const string serverType = "UserServer";
        loggerConfiguration
            .MinimumLevel.Is(ResolveMinimumLevel(hostingContext.Configuration))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverType)
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
    }

    private static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        return Enum.TryParse(configuration["logLevel"], true, out LogEventLevel level)
            ? level
            : LogEventLevel.Debug;
    }

    internal static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        services.AddSingleton<UserServerNodeOptions>();

        services.AddSingleton<NetworkService>();
        string natsEndpoint = hostContext.Configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("natsEndPoint is not configured.");
        services.AddSingleton<NatsClientFactory>(sp =>
            new NatsClientFactory(natsEndpoint, sp.GetRequiredService<ILogger<NatsClient>>()));
        services.AddSingleton<INatsClient>(sp => sp.GetRequiredService<NatsClientFactory>().Create());

        var redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton(_ => new RedisConnection(redisConfiguration));
        services.AddSingleton<IRedLockFactory>(sp =>
            sp.GetRequiredService<RedisConnection>().GetRedLockFactory());
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddSingleton<IPlayerSessionLeaseStore, RedisPlayerSessionLeaseStore>();
        services.AddGameEntryTicket(hostContext.Configuration);
        services.AddSingleton<IAccountCredentialStore, RedisAccountCredentialStore>();
        services.AddSingleton<AccountTokenService>();

        services.AddSingleton<IPlayerService, PlayerService>();

        services.AddSingleton<PlayerSessionRegistry>();
        services.AddSingleton<NatsPlayerSessionRouter>(sp => new NatsPlayerSessionRouter(
            sp.GetRequiredService<INatsClient>(),
            sp.GetRequiredService<PlayerSessionRegistry>().Get,
            sp.GetRequiredService<UserServerNodeOptions>().NodeId,
            sp.GetRequiredService<ILogger<NatsPlayerSessionRouter>>()));
        services.AddSingleton<IPlayerSessionRouter>(sp => sp.GetRequiredService<NatsPlayerSessionRouter>());

        services.AddSingleton<MatchingReservationService>();
        services.AddSingleton<MatchingQueue>();
        bool soloMapValidation = Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";
        services.AddSingleton<IGameServerRegistry, RedisGameServerRegistry>();
        services.AddSingleton<IGameServerAllocator, GameServerAllocator>();
        services.AddSingleton<BackgroundTaskTracker>(sp =>
            new BackgroundTaskTracker(sp.GetRequiredService<ILogger<BackgroundTaskTracker>>()));
        services.AddSingleton<MatchEntryService>();
        services.AddSingleton<IMatchEntryService>(sp => sp.GetRequiredService<MatchEntryService>());
        services.AddSingleton<MatchCreationService>(sp => new MatchCreationService(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<MatchingQueue>(),
            sp.GetRequiredService<MatchingReservationService>(),
            sp.GetRequiredService<IMatchEntryService>(),
            sp.GetRequiredService<IGameServerAllocator>(),
            soloMapValidation,
            sp.GetRequiredService<ILogger<MatchCreationService>>(),
            sp.GetRequiredService<BackgroundTaskTracker>().ShutdownToken));
        services.AddSingleton<MatchingLeaderLease>(sp => new MatchingLeaderLease(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<UserServerNodeOptions>().NodeId,
            sp.GetRequiredService<ILogger<MatchingLeaderLease>>()));
        services.AddSingleton<MatchingManager>();
        services.AddSingleton<IMatchingManager>(sp => sp.GetRequiredService<MatchingManager>());
        services.AddSingleton<MatchingLifecycleSubscriber>();

        services.AddSingleton<ServerReadinessState>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<UserServer>();
    }
}
