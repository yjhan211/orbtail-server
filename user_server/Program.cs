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
using user_server.services;
using user_server.services.scaling;

namespace user_server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(ConfigureApp)
            .ConfigureLogging(ConfigureLogging)
            .ConfigureServices(ConfigureServices)
            .RunConsoleAsync();
    }

    private static void ConfigureApp(HostBuilderContext _, IConfigurationBuilder config)
    {
        config.AddEnvironmentVariables();
    }

    private static void ConfigureLogging(HostBuilderContext hostingContext, ILoggingBuilder logging)
    {
        var serverConfig = CreateServerConfig(hostingContext.Configuration);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverConfig.ServerType)
            .Enrich.WithProperty("serverId", serverConfig.ServerId)
            .WriteTo.Console(outputTemplate:
                "[{Level:u3}] [ServerType:{serverType}] [ServerId:{serverId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        logging.ClearProviders();
        logging.AddSerilog(serilogLogger);
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        // 설정
        var serverConfig = CreateServerConfig(hostContext.Configuration);
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);
        UserServerClusterOptions clusterOptions = CreateClusterOptions(hostContext.Configuration);
        clusterOptions.Validate();
        services.AddSingleton(clusterOptions);
        services.AddSingleton(UserServerProcessIdentity.Create(clusterOptions));
        MatchingGameServerRoutingOptions gameServerRoutingOptions =
            CreateGameServerRoutingOptions(hostContext.Configuration);
        gameServerRoutingOptions.Validate();
        services.AddSingleton(gameServerRoutingOptions);

        // 네트워크/NATS
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();
        services.AddSingleton<ServerReadinessState>();

        // Redis
        RedisConfiguration redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton<IRedisConnectionPool>(sp => CreateRedisConnectionPool(sp, redisConfiguration));
        services.AddSingleton<IRedLockFactory>(sp =>
            sp.GetRequiredService<IRedisConnectionPool>().GetRedLockFactory());

        // 헬퍼
        services.AddSingleton<ICacheHelper, CacheHelper>();
        services.AddSingleton<IUserServerCoordinationStore, RedisUserServerCoordinationStore>();
        services.AddSingleton<IGameServerRoutingStore, RedisGameServerRoutingStore>();
        services.AddSingleton<MatchingLifecycleOutboxStore>();
        services.AddSingleton(CreateLogManager);
        services.AddManittoAuthenticationBoundaries(hostContext.Configuration);

        // 비즈니스 서비스
        services.AddSingleton<IPlayerService, PlayerService>();

        // 호스트 서비스
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<UserServer>();
    }

    private static ServerConfig CreateServerConfig(IConfiguration configuration)
    {
        return new ServerConfig
        {
            ServerType = configuration["serverType"] ?? "UserServer",
            GameServerNum = configuration.GetValue<int>("gameServerNum"),
            ServerId = 0
        };
    }

    private static UserServerClusterOptions CreateClusterOptions(IConfiguration configuration)
    {
        return new UserServerClusterOptions
        {
            Enabled = configuration.GetValue("userServerScaling:enabled", false),
            NodeId = configuration["userServerScaling:nodeId"] ?? string.Empty,
            HeartbeatInterval = TimeSpan.FromSeconds(
                configuration.GetValue("userServerScaling:heartbeatSeconds", 3)),
            NodeLeaseLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("userServerScaling:nodeLeaseSeconds", 12)),
            SessionOwnerLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("userServerScaling:sessionOwnerLeaseSeconds", 30)),
            MatchingLeaderHeartbeatInterval = TimeSpan.FromSeconds(
                configuration.GetValue("userServerScaling:matchingLeaderHeartbeatSeconds", 3)),
            MatchingLeaderLeaseLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("userServerScaling:matchingLeaderLeaseSeconds", 12)),
            DeliveryRequestTimeout = TimeSpan.FromMilliseconds(
                configuration.GetValue("userServerScaling:deliveryRequestTimeoutMilliseconds", 2000)),
            DeliveryRetryDelay = TimeSpan.FromMilliseconds(
                configuration.GetValue("userServerScaling:deliveryRetryDelayMilliseconds", 100)),
            DeliveryMaxAttempts = configuration.GetValue(
                "userServerScaling:deliveryMaxAttempts",
                3)
        };
    }

    private static MatchingGameServerRoutingOptions CreateGameServerRoutingOptions(
        IConfiguration configuration)
    {
        return new MatchingGameServerRoutingOptions
        {
            Enabled = configuration.GetValue("horizontalScaling:enabled", false),
            MaximumNodeAge = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:nodeLeaseSeconds", 12)),
            ReservationLifetime = TimeSpan.FromSeconds(
                configuration.GetValue("horizontalScaling:reservationSeconds", 300))
        };
    }

    private static LogManager CreateLogManager(IServiceProvider sp)
    {
        var serverConfig = sp.GetRequiredService<ServerConfig>();
        var logger = sp.GetRequiredService<ILogger<LogManager>>();
        return new LogManager(logger);
    }

    private static RedisConnectionPool CreateRedisConnectionPool(
        IServiceProvider sp,
        RedisConfiguration redisConfiguration)
    {
        var logger = sp.GetRequiredService<ILogger<RedisConnectionPool>>();
        var redisPool = new RedisConnectionPool(logger);
        redisPool.Initialize(redisConfiguration);
        return redisPool;
    }
}
