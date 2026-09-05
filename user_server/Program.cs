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
using user_server.network;
using user_server.services;
using ILogger = Microsoft.Extensions.Logging.ILogger;

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

    private static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        return Enum.TryParse(configuration["logLevel"], true, out LogEventLevel level)
            ? level
            : LogEventLevel.Debug;
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        // 서비스 클래스는 범주 없는 ILogger를 받는다 — 프로세스 로거 하나로 통일한다.
        services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("user_server"));
        services.AddSingleton<UserServerNodeIdentity>();

        // 네트워크/NATS — 라우터·lifecycle 구독은 NATS 연결 하나를 나눠 쓴다. 종료 시 구독자가 닫는다.
        services.AddSingleton<NetworkService>();
        string natsEndpoint = hostContext.Configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("natsEndPoint is not configured.");
        services.AddSingleton<NatsClientFactory>(sp =>
            new NatsClientFactory(natsEndpoint, sp.GetRequiredService<ILogger<NatsClient>>()));
        services.AddSingleton<INatsClient>(sp => sp.GetRequiredService<NatsClientFactory>().Create());

        // Redis
        var redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton(_ => new RedisConnection(redisConfiguration));
        services.AddSingleton<IRedLockFactory>(sp =>
            sp.GetRequiredService<RedisConnection>().GetRedLockFactory());

        // 공용 Redis 연산
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddSingleton<IPlayerSessionOwnershipStore, RedisPlayerSessionOwnershipStore>();
        services.AddSingleton<IMatchingQueueClaimStore, RedisMatchingQueueClaimStore>();
        services.AddGameHandoffTicket(hostContext.Configuration);
        services.AddAccountAuthentication(hostContext.Configuration);

        // 비즈니스 서비스
        services.AddSingleton<IPlayerService, PlayerService>();

        // 세션 — 로컬 세션 표와 N대 사이의 세션 라우터
        services.AddSingleton<UserSessionRegistry>();
        services.AddSingleton<NatsPlayerSessionRouter>(sp => new NatsPlayerSessionRouter(
            sp.GetRequiredService<INatsClient>(),
            sp.GetRequiredService<UserSessionRegistry>().Get,
            sp.GetRequiredService<UserServerNodeIdentity>().NodeId,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IPlayerSessionRouter>(sp => sp.GetRequiredService<NatsPlayerSessionRouter>());

        // 매칭 — 큐·claim·로스터·handoff·Game Server 배정·리더 lease. MatchingManager는 이들을 받아 수명만 조정한다.
        services.AddSingleton<MatchingQueueClaimCoordinator>();
        services.AddSingleton<MatchingQueue>();
        services.AddSingleton<MatchRosterBuilder>();
        services.AddSingleton(sp => DevMatchOverrides.FromEnvironment(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<IRedLockFactory>(),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IGameServerRegistry, RedisGameServerRegistry>();
        services.AddSingleton<IGameServerAllocator, GameServerAllocator>();
        services.AddSingleton<MatchingBackgroundOperations>();
        services.AddSingleton<MatchHandoffPublisher>(sp =>
        {
            var background = sp.GetRequiredService<MatchingBackgroundOperations>();
            return new MatchHandoffPublisher(
                sp.GetRequiredService<IRedisOperations>(),
                sp.GetRequiredService<GameHandoffTicketService>(),
                sp.GetRequiredService<MatchingQueueClaimCoordinator>(),
                sp.GetRequiredService<IPlayerSessionRouter>(),
                background.TryRun,
                background.ShutdownToken,
                sp.GetRequiredService<ILogger>());
        });
        services.AddSingleton<IMatchHandoffPublisher>(sp => sp.GetRequiredService<MatchHandoffPublisher>());
        services.AddSingleton<MatchmakingPass>(sp => new MatchmakingPass(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<MatchingQueue>(),
            sp.GetRequiredService<MatchingQueueClaimCoordinator>(),
            sp.GetRequiredService<MatchRosterBuilder>(),
            sp.GetRequiredService<IMatchHandoffPublisher>(),
            sp.GetRequiredService<IGameServerAllocator>(),
            sp.GetRequiredService<DevMatchOverrides>(),
            sp.GetRequiredService<MatchingBackgroundOperations>().ShutdownToken,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<MatchingLeaderLease>(sp => new MatchingLeaderLease(
            sp.GetRequiredService<IRedisOperations>(),
            sp.GetRequiredService<UserServerNodeIdentity>().NodeId,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<MatchingManager>();
        services.AddSingleton<IMatchingManager>(sp => sp.GetRequiredService<MatchingManager>());
        services.AddSingleton<MatchingLifecycleSubscriber>();

        // 호스트 서비스 — 등록 순서가 시작 순서, 종료는 역순
        services.AddSingleton<ServerReadinessState>();
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<UserServer>();
    }
}
