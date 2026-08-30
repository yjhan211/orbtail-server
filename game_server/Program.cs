using System.Text.RegularExpressions;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.contracts.scaling;
using network.core;
using network.hosting;
using network.infrastructure;
using network.infrastructure.authentication;
using network.infrastructure.scaling;
using network.interfaces;
using network.managers;
using Serilog;

namespace game_server;

internal static partial class Program
{
    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(ConfigureApp)
            .ConfigureLogging(ConfigureLogging)
            .ConfigureServices(ConfigureServices)
            .RunConsoleAsync();
    }

    private static int ExtractGameServerId(string podName)
    {
        var match = MyRegex().Match(podName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id)) return id + 1;
        return 0;
    }

    private static void ConfigureApp(HostBuilderContext _, IConfigurationBuilder config)
    {
        config.AddEnvironmentVariables();
    }

    private static void ConfigureLogging(HostBuilderContext hostingContext, ILoggingBuilder logging)
    {
        // 서버 구성 가져오기
        string serverType = hostingContext.Configuration["serverType"] ?? "GameServer";
        int serverId = ExtractGameServerId(hostingContext.Configuration["gameServerId"] ?? "");

        // Serilog 구성
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("serverId", serverId)
            .WriteTo.Console(
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [ServerType:{serverType}] [ServerId:{serverId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 기본 공급자 지우기
        logging.ClearProviders();

        // 로깅 파이프라인에 Serilog 추가
        logging.AddSerilog(serilogLogger);
    }


    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        var serverConfig = new ServerConfig
        {
            ServerType = hostContext.Configuration["serverType"] ?? "GameServer",
            GameServerNum = hostContext.Configuration.GetValue<int>("gameServerNum"),
            ServerId = ExtractGameServerId(hostContext.Configuration["gameServerId"] ?? "")
        };

        serverConfig.Validate();
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();
        services.AddSingleton<ServerReadinessState>();

        services.AddSingleton<LogManager>(sp =>
            new LogManager(sp.GetRequiredService<ILogger<LogManager>>()
            )
        );

        RedisConfiguration redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton<IRedisConnectionPool>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RedisConnectionPool>>();
            var redisPool = new RedisConnectionPool(logger);
            redisPool.Initialize(redisConfiguration);
            return redisPool;
        });
        services.AddSingleton<IRedLockFactory>(sp =>
            sp.GetRequiredService<IRedisConnectionPool>().GetRedLockFactory());
        services.AddSingleton<ICacheHelper, CacheHelper>();
        services.AddManittoAuthenticationBoundaries(hostContext.Configuration);
        GameServerScalingOptions scalingOptions = CreateScalingOptions(hostContext.Configuration);
        scalingOptions.Validate();
        services.AddSingleton(scalingOptions);
        services.AddSingleton<IGameServerRoutingStore, RedisGameServerRoutingStore>();
        services.AddSingleton<MatchingLifecycleOutboxStore>();
        services.AddSingleton<GameServerNodeLease>();
        // GameServer를 싱글턴으로 등록하여 HealthCheckService에서 어드민 endpoint용으로 주입 가능
        services.AddSingleton<GameServer>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameServer>());
    }

    private static GameServerScalingOptions CreateScalingOptions(IConfiguration configuration)
    {
        bool enabled = configuration.GetValue("horizontalScaling:enabled", false);
        int clientPort = configuration.GetValue("clientPort", 9001);
        return new GameServerScalingOptions
        {
            Enabled = enabled,
            NodeId = configuration["horizontalScaling:nodeId"] ??
                     configuration["gameServerId"] ??
                     string.Empty,
            PublicHost = configuration["horizontalScaling:publicHost"] ?? string.Empty,
            PublicPort = configuration.GetValue("horizontalScaling:publicPort", clientPort),
            MaxConcurrentMatches = configuration.GetValue("horizontalScaling:maxConcurrentMatches", 100),
            HeartbeatInterval = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:heartbeatIntervalSeconds", 3)),
            NodeLeaseLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:nodeLeaseSeconds", 12)),
            ReservationLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:reservationSeconds", 300)),
            ActiveOwnerLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:activeOwnerSeconds", 1800)),
            DrainTimeout = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:drainTimeoutSeconds", 30))
        };
    }

    [GeneratedRegex(@"-(\d+)$")]
    private static partial Regex MyRegex();
}
