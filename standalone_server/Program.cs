using game_server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.core;
using network.infrastructure;
using network.interfaces;
using network.managers;
using standalone_server.infrastructure;
using user_server;
using user_server.services;

namespace standalone_server;

internal static class Program
{
    private const short UserServerPort = 7000;
    private const short GameServerPort = 9001;
    private const int GameServerCount = 2;

    public static async Task<int> Main(string[] args)
    {
        SetStandaloneEnvironment();

        var redLockFactory = new InMemoryRedLockFactory();
        var cacheHelper = new InMemoryCacheHelper(redLockFactory);
        var natsClientFactory = new InMemoryNatsClientFactory();

        using IHost gameHost = BuildGameHost(cacheHelper, redLockFactory, natsClientFactory);
        using IHost userHost = BuildUserHost(cacheHelper, redLockFactory, natsClientFactory);

        try
        {
            await gameHost.StartAsync();
            await userHost.StartAsync();

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("  MANITTO SERVER READY");
            Console.WriteLine($"  User server : 127.0.0.1:{UserServerPort}");
            Console.WriteLine($"  Game server : 127.0.0.1:{GameServerPort}");
            Console.WriteLine("  마니또 클라이언트를 실행해 주세요.");
            Console.WriteLine("  플레이 중에는 이 창을 닫지 마세요.");
            Console.WriteLine("============================================================");
            Console.WriteLine();

            await Task.WhenAll(gameHost.WaitForShutdownAsync(), userHost.WaitForShutdownAsync());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("마니또 서버를 시작하지 못했습니다.");
            Console.Error.WriteLine($"{UserServerPort}, {GameServerPort} 포트를 다른 프로그램이 사용 중인지 확인해 주세요.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            await StopQuietlyAsync(userHost);
            await StopQuietlyAsync(gameHost);
        }
    }

    private static IHost BuildGameHost(
        ICacheHelper cacheHelper,
        IRedLockFactory redLockFactory,
        INatsClientFactory natsClientFactory)
    {
        var settings = new Dictionary<string, string?>
        {
            ["serverType"] = "GameServer",
            ["gameServerNum"] = GameServerCount.ToString(),
            ["clientPort"] = GameServerPort.ToString(),
            ["natsEndPoint"] = "in-memory"
        };

        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureLogging(ConfigureLogging)
            .ConfigureServices((_, services) =>
            {
                var serverConfig = new ServerConfig
                {
                    ServerType = "GameServer",
                    GameServerNum = GameServerCount,
                    ServerId = 1
                };
                serverConfig.Validate();

                services.AddSingleton<IServerConfig>(serverConfig);
                services.AddSingleton(serverConfig);
                services.AddSingleton<INetworkService, NetworkService>();
                services.AddSingleton(natsClientFactory);
                services.AddSingleton(cacheHelper);
                services.AddSingleton(redLockFactory);
                services.AddSingleton(sp => new LogManager(sp.GetRequiredService<ILogger<LogManager>>()));
                services.AddSingleton<GameServer>();
                services.AddHostedService(sp => sp.GetRequiredService<GameServer>());
            })
            .Build();
    }

    private static IHost BuildUserHost(
        ICacheHelper cacheHelper,
        IRedLockFactory redLockFactory,
        INatsClientFactory natsClientFactory)
    {
        var settings = new Dictionary<string, string?>
        {
            ["serverType"] = "UserServer",
            ["gameServerNum"] = GameServerCount.ToString(),
            ["servicePort"] = UserServerPort.ToString(),
            ["natsEndPoint"] = "in-memory"
        };

        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureLogging(ConfigureLogging)
            .ConfigureServices((_, services) =>
            {
                var serverConfig = new ServerConfig
                {
                    ServerType = "UserServer",
                    GameServerNum = GameServerCount,
                    ServerId = 0
                };
                serverConfig.Validate();

                services.AddSingleton<IServerConfig>(serverConfig);
                services.AddSingleton(serverConfig);
                services.AddSingleton<INetworkService, NetworkService>();
                services.AddSingleton(natsClientFactory);
                services.AddSingleton(cacheHelper);
                services.AddSingleton(redLockFactory);
                services.AddSingleton(sp => new LogManager(sp.GetRequiredService<ILogger<LogManager>>()));
                services.AddSingleton<IPlayerService, PlayerService>();
                services.AddSingleton<UserServer>();
                services.AddHostedService(sp => sp.GetRequiredService<UserServer>());
            })
            .Build();
    }

    private static void ConfigureLogging(HostBuilderContext _, ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        logging.SetMinimumLevel(LogLevel.Information);
    }

    private static void SetStandaloneEnvironment()
    {
        Environment.SetEnvironmentVariable("GAME_SERVER_IP", "127.0.0.1");
        Environment.SetEnvironmentVariable("GAME_SERVER_PORT", GameServerPort.ToString());
        Environment.SetEnvironmentVariable("TEST_TWO_PLAYER_MATCH", "0");
        Environment.SetEnvironmentVariable("DISABLE_GAME_END", "0");
    }

    private static async Task StopQuietlyAsync(IHost host)
    {
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Preserve the original startup error while shutting down partial hosts.
        }
    }
}
