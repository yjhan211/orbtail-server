using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
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
        // 설정
        var serverConfig = CreateServerConfig(hostContext.Configuration);
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);

        // 네트워크/NATS
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();

        // Redis
        services.AddSingleton<RedisConnectionPool>(sp => CreateRedisConnectionPool(sp, hostContext));
        services.AddSingleton<IRedisConnectionPool>(sp => sp.GetRequiredService<RedisConnectionPool>());
        services.AddSingleton<IRedLockFactory>(sp => sp.GetRequiredService<RedisConnectionPool>().GetRedLockFactory());

        // 헬퍼
        services.AddSingleton<ICacheHelper, CacheHelper>();
        services.AddSingleton(CreateLogManager);

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

    private static LogManager CreateLogManager(IServiceProvider sp)
    {
        var serverConfig = sp.GetRequiredService<ServerConfig>();
        var logger = sp.GetRequiredService<ILogger<LogManager>>();
        return new LogManager(logger);
    }

    private static RedisConnectionPool CreateRedisConnectionPool(IServiceProvider sp, HostBuilderContext hostContext)
    {
        var logger = sp.GetRequiredService<ILogger<RedisConnectionPool>>();
        var redisPool = new RedisConnectionPool(logger);
        string redisEndpoints = hostContext.Configuration["redisEndpoints"]
                                ?? throw new InvalidOperationException("RedisEndpoints is not configured.");

        redisPool.Initialize(redisEndpoints);
        return redisPool;
    }
}
