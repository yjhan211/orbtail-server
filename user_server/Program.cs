using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.infrastructure;
using network.managers;

namespace user_server;

public static class Program
{
    public static int GameServerNum { get; private set; }

    public static async Task Main(string[] args)
    {
        await Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) => { config.AddEnvironmentVariables(); })
            .ConfigureLogging((hostingContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                logging.AddConsole();
            })
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<NetworkService>();
                services.AddSingleton<RedisConnectionPool>();
                services.AddSingleton<NatsClientFactory>();
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var serverType = config["ServerType"] ?? "UserServer";
                    GameServerNum = config.GetValue<int>("GameServerNum");
                    return new LogManager(serverType, 0);
                });
                services.AddHostedService<UserServer>();
            })
            .RunConsoleAsync();

        // GameDataHelper.Initialize(new LogManager("", 0));
    }
}