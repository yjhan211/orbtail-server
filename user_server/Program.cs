using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.managers;
using network.infrastructure;

namespace user_server
{
    class Program
    {
        public static int GameServerNum { get; private set; }
        public static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddEnvironmentVariables();
            })
            .ConfigureLogging((hostingContext, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                logging.AddConsole();
            })
            .ConfigureServices((hostingContext, services) =>
            {
                services.AddSingleton<NetworkService>();
                services.AddSingleton<RedisConnectionPool>();
                services.AddSingleton<NatsClientFactory>();
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var serverType = config["ServerType"] ?? "none";
                    GameServerNum = config.GetValue<int>("GameServerNum");
                    return new LogManager(serverType, GameServerNum);
                });
                services.AddHostedService<UserServer>();
            })
            .RunConsoleAsync();
        }
    }
}
