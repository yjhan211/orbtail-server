using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.config;
using network.core;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using Serilog;
using user_server.services;

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
        RegisterConfigurationServices(services, hostContext);
        RegisterCoreServices(services);
        RegisterInfrastructureServices(services, hostContext);
        RegisterHelperServices(services);
        RegisterUserServerServices(services);

        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<UserServer>();
    }

    private static void RegisterConfigurationServices(IServiceCollection services, HostBuilderContext hostContext)
    {
        var serverConfig = CreateServerConfig(hostContext.Configuration);
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.AddSingleton<NetworkService>();
        services.AddSingleton<INetworkService>(provider => provider.GetRequiredService<NetworkService>());
        services.AddSingleton<NatsClientFactory>();
        services.AddSingleton<INatsClientFactory>(provider => provider.GetRequiredService<NatsClientFactory>());
        services.AddSingleton(CreateLogManager);
    }

    private static void RegisterInfrastructureServices(IServiceCollection services, HostBuilderContext hostContext)
    {
        services.AddSingleton<RedisConnectionPool>(provider => CreateRedisConnectionPool(provider, hostContext));
        services.AddSingleton<IRedisConnectionPool>(provider => provider.GetRequiredService<RedisConnectionPool>());
        services.AddSingleton<IRedLockFactory>(provider => provider.GetRequiredService<RedisConnectionPool>().GetRedLockFactory());
    }

    private static void RegisterHelperServices(IServiceCollection services)
    {
        services.AddSingleton<CacheHelper>();
        services.AddSingleton<ICacheHelper>(provider => provider.GetRequiredService<CacheHelper>());
    }

    private static void RegisterUserServerServices(IServiceCollection services)
    {
        // PlayerService: 플레이어 관련 로직 (인벤토리, 퀘스트, 메일)
        services.AddSingleton<PlayerService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<PlayerService>>();
            var cacheHelper = provider.GetRequiredService<ICacheHelper>();
            var redLock = provider.GetRequiredService<IRedLockFactory>();
            return new PlayerService(logger, cacheHelper, redLock);
        });

        // MatchingManager: 매칭 큐 관리
        // Note: Will be registered by UserServer with getSession callback
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

    private static LogManager CreateLogManager(IServiceProvider serviceProvider)
    {
        var serverConfig = serviceProvider.GetRequiredService<ServerConfig>();
        var logger = serviceProvider.GetRequiredService<ILogger<LogManager>>();

        return new LogManager(
            serverConfig.ServerType,
            serverConfig.ServerId,
            logger
        );
    }

    private static RedisConnectionPool CreateRedisConnectionPool(IServiceProvider _, HostBuilderContext hostContext)
    {
        var redisPool = new RedisConnectionPool();
        var redisEndpoints = hostContext.Configuration["redisEndpoints"]
            ?? throw new InvalidOperationException("RedisEndpoints is not configured.");

        redisPool.Initialize(redisEndpoints);
        return redisPool;
    }
}
