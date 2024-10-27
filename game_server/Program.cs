using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.infrastructure;
using network.managers;

namespace game_server;

internal static class Program
{
    public static int GameServerNum { get; private set; }
    public static int GameServerId { get; private set; }

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
                    var serverType = config["serverType"] ?? "GameServer";
                    GameServerNum = config.GetValue<int>("gameServerNum");
                    GameServerId = ExtractGameServerId(config["gameServerId"] ?? "");
                    if (GameServerId <= 0) throw new Exception($"Invalid Game Server Id: {GameServerId}");
                    return new LogManager(serverType, GameServerId);
                });
                services.AddHostedService<GameServer>();
            })
            .RunConsoleAsync();
    }

    private static int ExtractGameServerId(string podName)
    {
        var match = Regex.Match(podName, @"-(\d+)$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id)) return id + 1;
        return 0;
    }
}