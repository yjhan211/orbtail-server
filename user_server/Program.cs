using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using network.core;
using network.gamehandoff;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using network.managers;
using Serilog;
using Serilog.Events;
using user_server.services;

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
        string serverType = hostingContext.Configuration["serverType"] ?? "UserServer";
        loggerConfiguration
            .MinimumLevel.Is(ResolveMinimumLevel(hostingContext.Configuration))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverType)
            .WriteTo.Console(outputTemplate:
                "[{Level:u3}] [ServerType:{serverType}] {Message:lj}{NewLine}{Exception}");
    }

    // logLevel 설정(예: Information)으로 재빌드 없이 최소 로그 레벨 조정. 미지정 시 기존 기본값 Debug
    private static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        return Enum.TryParse(configuration["logLevel"], true, out LogEventLevel level)
            ? level
            : LogEventLevel.Debug;
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        // 네트워크/NATS
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();
        services.AddSingleton<ServerReadinessState>();

        // Redis
        RedisConfiguration redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton<IRedisConnection>(_ => CreateRedisConnection(redisConfiguration));
        services.AddSingleton<IRedLockFactory>(sp =>
            sp.GetRequiredService<IRedisConnection>().GetRedLockFactory());

        // 공용 Redis 연산
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddSingleton<IPlayerSessionOwnershipStore, RedisPlayerSessionOwnershipStore>();
        services.AddSingleton<IMatchingQueueClaimStore, RedisMatchingQueueClaimStore>();
        services.AddSingleton<LogManager>();
        services.AddGameHandoffTicket(hostContext.Configuration);
        services.AddAccountAuthentication(hostContext.Configuration);

        // 비즈니스 서비스
        services.AddSingleton<IPlayerService, PlayerService>();

        // 호스트 서비스
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<UserServer>();
    }

    private static RedisConnection CreateRedisConnection(RedisConfiguration redisConfiguration)
    {
        var redisConnection = new RedisConnection();
        redisConnection.Initialize(redisConfiguration);
        return redisConnection;
    }
}
