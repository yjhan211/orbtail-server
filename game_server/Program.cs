using System.Text.RegularExpressions;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.gamehandoff;
using network.hosting;
using network.infrastructure;
using network.infrastructure.routing;
using network.interfaces;
using network.managers;
using Serilog;
using Serilog.Events;

namespace game_server;

internal static partial class Program
{
    private static bool _warnedPodNameParseFailure;

    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .UseSerilog(ConfigureSerilog)
            .ConfigureServices(ConfigureServices)
            .RunConsoleAsync();
    }

    private static int ExtractGameServerId(string podName)
    {
        // 파드 이름 끝의 서수(0-based)를 1-based ID로 변환. 빈 값은 로컬 개발 경로라 폴백이 정상
        if (string.IsNullOrEmpty(podName)) return 0;

        var match = PodOrdinalRegex().Match(podName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id)) return id + 1;

        if (!_warnedPodNameParseFailure)
        {
            _warnedPodNameParseFailure = true;
            Console.Error.WriteLine($"[WRN] gameServerId '{podName}'에서 파드 서수를 찾지 못해 serverId 0으로 폴백");
        }

        return 0;
    }

    private static void ConfigureSerilog(HostBuilderContext hostingContext, LoggerConfiguration loggerConfiguration)
    {
        var serverConfig = CreateServerConfig(hostingContext.Configuration);
        loggerConfiguration
            .MinimumLevel.Is(ResolveMinimumLevel(hostingContext.Configuration))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.WithProperty("serverType", serverConfig.ServerType)
            .Enrich.WithProperty("serverId", serverConfig.ServerId)
            .WriteTo.Console(
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [ServerType:{serverType}] [ServerId:{serverId}] {Message:lj}{NewLine}{Exception}");
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        var serverConfig = CreateServerConfig(hostContext.Configuration);
        serverConfig.Validate();
        services.AddSingleton<IServerConfig>(serverConfig);
        services.AddSingleton(serverConfig);
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<INatsClientFactory, NatsClientFactory>();
        services.AddSingleton<ServerReadinessState>();
        services.AddSingleton<LogManager>();

        var redisConfiguration = RedisConfigurationParser.Parse(hostContext.Configuration);
        services.AddSingleton(redisConfiguration);
        services.AddSingleton<IRedisConnection>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RedisConnection>>();
            var redisConnection = new RedisConnection(logger);
            redisConnection.Initialize(redisConfiguration);
            return redisConnection;
        });
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

    private static ServerConfig CreateServerConfig(IConfiguration configuration)
    {
        return new ServerConfig
        {
            ServerType = configuration["serverType"] ?? "GameServer",
            GameServerNum = configuration.GetValue<int>("gameServerNum"),
            GameServerNodeId = configuration["gameServerId"] ?? "",
            ServerId = ExtractGameServerId(configuration["gameServerId"] ?? "")
        };
    }

    /// <summary>
    ///     레지스트리에 광고할 공개 주소·용량. 공개 host는 클라이언트가 실제로 접속할 주소라 기본값이 없다.
    /// </summary>
    private static GameServerNodeOptions CreateGameServerNodeOptions(IConfiguration configuration)
    {
        return new GameServerNodeOptions
        {
            PublicHost = configuration["GAME_SERVER_PUBLIC_HOST"] ?? "",
            PublicPort = configuration.GetValue("GAME_SERVER_PUBLIC_PORT", GameServerNodeOptions.DefaultPublicPort),
            MaxConcurrentMatches = configuration.GetValue(
                "GAME_SERVER_MAX_MATCHES",
                GameServerNodeOptions.DefaultMaxConcurrentMatches)
        };
    }

    [GeneratedRegex(@"-(\d+)$")]
    private static partial Regex PodOrdinalRegex();
}
