using game_server.network;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.gamehandoff;
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
        services.AddGameHandoffTicket(hostContext.Configuration);

        var devOptions = GameServerDevOptions.FromConfiguration(hostContext.Configuration);
        devOptions.Validate(hostContext.HostingEnvironment.IsDevelopment());
        services.AddSingleton(devOptions);

        services.AddSingleton<IGameServerRegistry, RedisGameServerRegistry>();
        services.AddSingleton<GameSessionRegistry>();
        services.AddSingleton<MatchingLifecycleService>(sp => new MatchingLifecycleService(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<INatsClient>(),
            sp.GetRequiredService<ILogger<MatchingLifecycleService>>()));
        services.AddSingleton<GameServer>();

        services.AddSingleton<ServerReadinessState>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameServer>());
    }
}
