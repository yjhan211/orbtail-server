using System.Reflection;
using game_server;
using game_server.matches;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
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
    public void CombatDamageStateAndCriticalRandomBelongToEachMatchButTheServiceIsShared()
    {
        using var provider = CreateProvider();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var first = store.GetOrCreate(990011);
        var second = store.GetOrCreate(990012);

        Assert.Same(provider.GetRequiredService<MatchCombatDamageService>(), provider.GetRequiredService<MatchCombatDamageService>());
        Assert.Same(first.CombatDamage, store.GetOrThrow(first.MatchingId).CombatDamage);
        Assert.NotSame(first.CombatDamage, second.CombatDamage);
        Assert.NotSame(first.CombatDamage.CriticalRng, second.CombatDamage.CriticalRng);
    }

    [Fact]
    public void MatchTickServiceSharesSingletonServicesAcrossMatches()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<MatchTickService>();
        var factory = provider.GetRequiredService<Func<MatchRuntime, TimeProvider, MatchTickLoop>>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var first = store.GetOrCreate(990001);
        var second = store.GetOrCreate(990002);
        var firstLoop = factory(first, TimeProvider.System);
        var secondLoop = factory(second, TimeProvider.System);
        try
        {
            Assert.NotSame(firstLoop, secondLoop);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.Same(factory, typeof(MatchTickService).GetFields(flags)
                .Single(field => field.FieldType == factory.GetType()).GetValue(service));
            foreach (var loop in new[] { firstLoop, secondLoop })
            {
                Type[] dependencyTypes =
                [
                    typeof(MatchCombatService),
                    typeof(game_server.matches.MatchFieldService),
                    typeof(MatchMoveService)
                ];
                foreach (var type in dependencyTypes)
                {
                    var dependency = typeof(MatchTickLoop).GetFields(flags)
                        .Single(field => field.FieldType == type).GetValue(loop);
                    var firstDependency = typeof(MatchTickLoop).GetFields(flags)
                        .Single(field => field.FieldType == type).GetValue(firstLoop);
                    var secondDependency = typeof(MatchTickLoop).GetFields(flags)
                        .Single(field => field.FieldType == type).GetValue(secondLoop);
                    Assert.Same(provider.GetRequiredService(type), dependency);
                    Assert.Same(firstDependency, secondDependency);
                }
                Assert.DoesNotContain(typeof(MatchTickLoop).GetFields(flags),
                    field => typeof(Delegate).IsAssignableFrom(field.FieldType));
            }
            Assert.DoesNotContain(typeof(GameServer).GetConstructors().Single().GetParameters(),
                parameter => parameter.ParameterType == typeof(MatchCombatService));
        }
        finally
        {
            firstLoop.Stop();
            secondLoop.Stop();
        }
    }
    [Fact]
    public void GameServerUsesTheRegistrySingletonRegisteredByProgram()
    {
        using var provider = CreateProvider();
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
            typeof(game_server.matches.MatchRuntimeStore),
            typeof(game_server.matches.GameMatchEntryService),
            typeof(game_server.matches.MatchEntryFailureHandler),
            typeof(game_server.matches.MatchTickService)
        ];
        foreach (var type in serviceTypes)
        {
            var field = typeof(GameServer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == type);
            Assert.Same(provider.GetRequiredService(type), field.GetValue(server));
        }
    }
    [Fact]
    public void HealthServiceIsSharedWithoutOwningPlayerOrMatchState()
    {
        using var provider = CreateProvider();
        var health = provider.GetRequiredService<PlayerHealthService>();
        Assert.Same(health, provider.GetRequiredService<PlayerHealthService>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var type in new[] { typeof(MatchCombatService), typeof(game_server.matches.MatchOrbAttackService),
                     typeof(game_server.matches.MatchFieldService), typeof(game_server.players.PlayerPickupService) })
        {
            var service = provider.GetRequiredService(type);
            var field = type.GetFields(flags).Single(field => field.FieldType == typeof(PlayerHealthService));
            Assert.Same(health, field.GetValue(service));
        }
        Assert.DoesNotContain(typeof(PlayerHealthService).GetFields(flags),
            field => field.FieldType == typeof(Player) || field.FieldType == typeof(MatchRuntime));
        Assert.DoesNotContain(typeof(GameClientSession).GetConstructors(flags | BindingFlags.Public)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(PlayerHealthService) || parameter.ParameterType == typeof(PlayerEliminationService));
    }

    internal static ServiceProvider CreateProvider(network.infrastructure.messaging.INatsClient? natsClient = null,
        Action<IServiceCollection>? configure = null)
    {
        // 서비스가 맵·CSV를 읽으므로 테스트 실행 순서와 무관하게 여기서 로드한다.
        UserServerMatchingTestData.EnsureGameDataLoaded();
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

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
