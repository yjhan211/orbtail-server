using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.core.abstractions;
using network.gamehandoff;
using network.hosting;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using network.infrastructure.routing;
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
        IConfiguration configuration = hostingContext.Configuration;
        string serverType = configuration["serverType"] ?? "GameServer";
        string nodeId = configuration["gameServerId"] ?? "unconfigured";
        loggerConfiguration
            .MinimumLevel.Is(ResolveMinimumLevel(hostingContext.Configuration))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("nodeId", nodeId)
            .WriteTo.Console(
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [ServerType:{serverType}] [NodeId:{nodeId}] {Message:lj}{NewLine}{Exception}");
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        services.AddSingleton<INetworkService, NetworkService>();
        string natsEndpoint = hostContext.Configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("natsEndPoint is not configured.");
        services.AddSingleton<NatsClientFactory>(sp =>
            new NatsClientFactory(natsEndpoint, sp.GetRequiredService<ILogger<NatsClient>>()));
        services.AddSingleton<ServerReadinessState>();

        var redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton(_ => new RedisConnection(redisConfiguration));
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddGameHandoffTicket(hostContext.Configuration);
        var devOptions = GameServerDevOptions.FromConfiguration(hostContext.Configuration);
        devOptions.Validate(hostContext.HostingEnvironment.IsDevelopment());
        services.AddSingleton(devOptions);
        var nodeOptions = CreateGameServerNodeOptions(hostContext.Configuration);
        nodeOptions.Validate();
        services.AddSingleton(nodeOptions);
        services.AddSingleton<IGameServerRegistry, RedisGameServerRegistry>();
        services.AddSingleton<GameServer>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameServer>());
    }

    // logLevel 설정(예: Information)으로 재빌드 없이 최소 로그 레벨 조정. 미지정 시 기존 기본값 Debug
    private static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        return Enum.TryParse(configuration["logLevel"], true, out LogEventLevel level)
            ? level
            : LogEventLevel.Debug;
    }

    /// <summary>
    ///     레지스트리에 광고할 공개 주소·용량. 공개 host는 클라이언트가 실제로 접속할 주소라 기본값이 없다.
    /// </summary>
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
}
