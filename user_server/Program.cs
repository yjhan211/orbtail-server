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
        // 서버 구성 가져오기
        var serverType = hostingContext.Configuration["serverType"] ?? "UserServer";
        var serverId = 0;
    
        // Serilog 구성
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("serverId", serverId)
            .WriteTo.Console(outputTemplate: 
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
            ServerType = hostContext.Configuration["serverType"] ?? "UserServer",
            GameServerNum = hostContext.Configuration.GetValue<int>("gameServerNum"),
            ServerId = 0
        };
    
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);
        services.AddSingleton<NetworkService>();
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<NatsClientFactory>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();
    
        // LogManager 등록 방법 변경
        services.AddSingleton<LogManager>(sp => 
            new LogManager(
                serverConfig.ServerType, 
                serverConfig.ServerId, 
                sp.GetRequiredService<ILogger<LogManager>>()
            )
        );

        services.AddSingleton<RedisConnectionPool>(sp => {
            var redisPool = new RedisConnectionPool();
            var redisEndpoints = hostContext.Configuration["redisEndpoints"] ?? throw new InvalidOperationException("RedisEndpoints is not configured.");
            redisPool.Initialize(redisEndpoints);
            return redisPool;
        });
        services.AddSingleton<IRedisConnectionPool>(sp => sp.GetRequiredService<RedisConnectionPool>());
        services.AddSingleton<CacheHelper>();
        services.AddSingleton<ICacheHelper, CacheHelper>();
        services.AddHostedService<UserServer>();
    }
}