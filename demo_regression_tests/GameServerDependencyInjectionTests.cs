using game_server.matches.bots;
using game_server.matches.logging;
using game_server.players;
using game_server.matches.combat;
using game_server.matches.entry;
using game_server.matches.results;
using game_server.matches;
using game_server.sessions;
using System.Reflection;
using game_server;
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
    public void MatchTickServiceCreatesSeparateLoopsUsingSharedGameplayServices()
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
                    typeof(MatchCountdownService), typeof(MatchCombatService),
                    typeof(game_server.matches.field.MatchEnvironmentService),
                    typeof(game_server.matches.bots.BotMovementService), typeof(game_server.matches.bots.BotDecisionService),
                    typeof(game_server.matches.field.MatchZoneService)
                ];
                foreach (var type in dependencyTypes)
                {
                    var dependency = typeof(MatchTickLoop).GetFields(flags)
                        .Single(field => field.FieldType == type).GetValue(loop);
                    Assert.Same(provider.GetRequiredService(type), dependency);
                }
                Assert.DoesNotContain(typeof(MatchTickLoop).GetFields(flags),
                    field => typeof(Delegate).IsAssignableFrom(field.FieldType));
            }
            Assert.DoesNotContain(typeof(GameServer).GetConstructors().Single().GetParameters(),
                parameter => parameter.ParameterType == typeof(MatchCombatService)
                    || parameter.ParameterType == typeof(MatchCountdownService));
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
        // 로그를 먼저 요청해도 저장소와 순환 없이 조립되어야 한다.
        provider.GetRequiredService<game_server.matches.logging.GameEventLogManager>();
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
            typeof(game_server.matches.logging.GameEventLogManager),
            typeof(game_server.matches.results.MatchEliminationService),
            typeof(game_server.matches.entry.GameMatchEntryService),
            typeof(game_server.players.MovementValidationService),
            typeof(game_server.matches.entry.MatchEntryFailureHandler),
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
    public void MatchResultServiceDoesNotOwnAConnectionOrDependOnGameServer()
    {
        using var provider = CreateProvider();
        var results = provider.GetRequiredService<game_server.matches.results.MatchResultService>();
        Assert.Same(results, provider.GetRequiredService<game_server.matches.results.MatchResultService>());

        var fields = typeof(game_server.matches.results.MatchResultService)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameServer));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(game_server.sessions.GameClientSession));
        Assert.DoesNotContain(
            typeof(game_server.sessions.GameClientSession)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(game_server.matches.results.MatchSummaryFileStore));
    }
    internal static ServiceProvider CreateProvider(network.infrastructure.messaging.INatsClient? natsClient = null,
        Action<IServiceCollection>? configure = null)
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

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
