using System.Reflection;
using game_server;
using game_server.network;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using network.infrastructure.redis;

namespace demo_regression_tests;

public sealed class GameServerDependencyInjectionTests
{
    [Fact]
    public void GameServerUsesTheRegistrySingletonRegisteredByProgram()
    {
        using var provider = CreateProvider();
        // 로그를 먼저 요청해도 저장소와 순환 없이 조립되어야 한다.
        provider.GetRequiredService<game_server.services.GameEventLogManager>();
        var registry = provider.GetRequiredService<GameSessionRegistry>();
        var server = provider.GetRequiredService<GameServer>();
        var injectedRegistry = typeof(GameServer)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(GameSessionRegistry))
            .GetValue(server);

        Assert.Same(registry, provider.GetRequiredService<GameSessionRegistry>());
        Assert.Same(registry, injectedRegistry);
        Assert.Same(server, provider.GetRequiredService<GameServer>());
        Type[] serviceTypes =
        [
            typeof(game_server.services.MatchRuntimeStore),
            typeof(game_server.services.GameEventLogManager),
            typeof(game_server.services.MatchSummaryFileStore),
            typeof(game_server.services.MatchEntryFailureHandler),
            typeof(game_server.services.MatchCleanupService),
            typeof(game_server.services.BotEliminationService),
            typeof(game_server.services.MatchCountdownService),
            typeof(game_server.services.GameServerTickService),
            typeof(game_server.services.BotMovementService)
        ];
        foreach (var type in serviceTypes)
        {
            var field = typeof(GameServer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == type);
            Assert.Same(provider.GetRequiredService(type), field.GetValue(server));
        }
    }
    internal static ServiceProvider CreateProvider(network.infrastructure.messaging.INatsClient? natsClient = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["gameServerId"] = "test-game-node",
                ["GAME_SERVER_PUBLIC_HOST"] = "127.0.0.1",
                ["natsEndPoint"] = "nats://unused:4222"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        game_server.Program.ConfigureServices(new HostBuilderContext(new Dictionary<object, object>())
        {
            Configuration = configuration,
            HostingEnvironment = new HostingEnvironment { EnvironmentName = Environments.Production }
        }, services);
        // DI 조립만 검사한다. Redis·NATS 연결이나 서버 시작은 실행하지 않는다.
        services.RemoveAll<network.infrastructure.messaging.INatsClient>();
        services.AddSingleton<network.infrastructure.messaging.INatsClient>(natsClient ?? new MatchStartCountdownPublicationTests.NoOpNatsClient());
        services.RemoveAll<IRedisOperations>();
        services.AddSingleton<IRedisOperations>(new InMemoryRedisOperations());

        return services.BuildServiceProvider();
    }
}
