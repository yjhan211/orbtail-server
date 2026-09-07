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
        // DI 조립만 검사한다. Redis 연결이나 서버 시작은 실행하지 않는다.
        services.RemoveAll<IRedisOperations>();
        services.AddSingleton<IRedisOperations>(new InMemoryRedisOperations());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<GameSessionRegistry>();
        var server = provider.GetRequiredService<GameServer>();
        var injectedRegistry = typeof(GameServer)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(GameSessionRegistry))
            .GetValue(server);

        Assert.Same(registry, provider.GetRequiredService<GameSessionRegistry>());
        Assert.Same(registry, injectedRegistry);
        Assert.Same(server, provider.GetRequiredService<GameServer>());
    }
}
