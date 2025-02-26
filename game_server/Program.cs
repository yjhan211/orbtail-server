using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.infrastructure;
using network.managers;
using network.config;
using network.helpers;

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
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id)) return id + 1;
        return 0;
    }
    
    private static void ConfigureApp(HostBuilderContext _, IConfigurationBuilder config)
    {
        config.AddEnvironmentVariables();
    }

    private static void ConfigureLogging(HostBuilderContext hostingContext, ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
        logging.AddConsole();
    }

    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        var serverConfig = new ServerConfig
        {
            ServerType = hostContext.Configuration["serverType"] ?? "GameServer",
            GameServerNum = hostContext.Configuration.GetValue<int>("gameServerNum"),
            GameServerId = ExtractGameServerId(hostContext.Configuration["gameServerId"] ?? "")
        };
        
        serverConfig.Validate();

        services.AddSingleton(serverConfig);
        services.AddSingleton<NetworkService>();
        services.AddSingleton<NatsClientFactory>();
        services.AddSingleton(sp => new LogManager(serverConfig.ServerType, serverConfig.GameServerId));
        services.AddHostedService<GameServer>();
        services.AddSingleton<CacheHelper>(sp => 
        {
            var redisPool = new RedisConnectionPool();
            var redisEndpoints = hostContext.Configuration["redisEndpoints"] ?? throw new InvalidOperationException("RedisEndpoints is not configured.");
            redisPool.Initialize(redisEndpoints);
            return new CacheHelper(redisPool);
        });
    }

    [GeneratedRegex(@"-(\d+)$")]
    private static partial Regex MyRegex();
}