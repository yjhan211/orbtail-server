using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.config;
using network.core;
using network.helpers;
using network.infrastructure;
using network.managers;

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
        logging.ClearProviders();
        logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
        logging.AddConsole();
    }
    
    private static void ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
    {
        var serverConfig = new ServerConfig
        {
            ServerType = hostContext.Configuration["serverType"] ?? "UserServer",
            GameServerNum = hostContext.Configuration.GetValue<int>("gameServerNum"),
            GameServerId = 0
        };
        
        serverConfig.Validate();

        services.AddSingleton(serverConfig);
        services.AddSingleton<NetworkService>();
        services.AddSingleton<NatsClientFactory>();
        services.AddSingleton(sp => new LogManager(serverConfig.ServerType, serverConfig.GameServerId));
        services.AddHostedService<UserServer>();
        services.AddSingleton<CacheHelper>(sp => 
        {
            var redisPool = new RedisConnectionPool();
            var redisEndpoints = hostContext.Configuration["redisEndpoints"] ?? throw new InvalidOperationException("RedisEndpoints is not configured.");
            redisPool.Initialize(redisEndpoints);
            return new CacheHelper(redisPool);
        });
    }
}