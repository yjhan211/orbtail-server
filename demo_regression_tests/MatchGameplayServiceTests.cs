using System.Reflection;
using game_server;
using game_server.services;
using Microsoft.Extensions.DependencyInjection;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchGameplayServiceTests
{
    [Fact]
    public void Composition_SharesServicesWithoutDependingBackOnHost()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var decisions = provider.GetRequiredService<BotDecisionService>();
        var arena = provider.GetRequiredService<MatchArenaService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        Assert.Same(arena, provider.GetRequiredService<MatchArenaService>());
        Assert.Same(decisions, Read<BotDecisionService>(arena));
        Assert.Same(store, Read<MatchRuntimeStore>(arena));
        Assert.Same(store, Read<MatchRuntimeStore>(decisions));
        Assert.DoesNotContain(Fields(arena), field => field.FieldType == typeof(GameServer));
        Assert.DoesNotContain(Fields(decisions), field =>
            field.FieldType == typeof(GameServer) || field.FieldType == typeof(MatchArenaService));
    }

    [Fact]
    public void Arena_SkipsMissingAndTerminalMatchesWithoutRecreatingThem()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var arena = provider.GetRequiredService<MatchArenaService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        arena.ProcessSwarmArenaForMatching(947701, []);
        Assert.Null(store.Get(947701));
        var match = store.GetOrCreate(947702);
        using (store.Enter(match))
        {
            match.TryMarkTerminal();
            arena.ProcessSwarmArenaForMatching(match.MatchingId, []);
        }
        Assert.Null(store.Get(match.MatchingId));
        arena.ProcessSwarmArenaForMatching(match.MatchingId, []);
        Assert.Null(store.Get(match.MatchingId));
    }

    [Fact]
    public void BotRecovery_RespectsHitGraceAndKeepsMatchClocksSeparate()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var first = store.GetOrCreate(947703);
        var second = store.GetOrCreate(947704);
        var now = DateTime.UtcNow;
        using (store.Enter(first))
        {
            var bot = new BotPlayerState { PlayerId = 11, Health = 10 };
            first.Swarm.BotTactics.LastDamagedAtUtc[(first.MatchingId, 11)] = now;
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(5));
            Assert.Equal(10, bot.Health);
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Health);
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Health);
        }
        using (store.Enter(second))
        {
            var bot = new BotPlayerState { PlayerId = 11, Health = 10 };
            service.ProcessSwarmBotRecovery(second.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Health);
            second.TryMarkTerminal();
        }
        using (store.Enter(first)) first.TryMarkTerminal();
    }

    [Fact]
    public void BotCut_UsesProvidedCostAndMatchCooldown()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947705);
        var now = DateTime.UtcNow;
        using (store.Enter(match))
        {
            int half = (int)(network.common.Config.MAX_HEALTH * 0.5f);
            Assert.True(service.IsSwarmBotCutAllowed(match.MatchingId, 11, half + 5, now, 5));
            Assert.False(service.IsSwarmBotCutAllowed(match.MatchingId, 11, half + 5, now, 6));
            match.Swarm.BotTactics.LastTrailCutAtUtc[(match.MatchingId, 11)] = now;
            Assert.False(service.IsSwarmBotCutAllowed(match.MatchingId, 11, network.common.Config.MAX_HEALTH, now.AddSeconds(5), 5));
            Assert.True(service.IsSwarmBotCutAllowed(match.MatchingId, 11, network.common.Config.MAX_HEALTH, now.AddSeconds(6), 5));
            match.TryMarkTerminal();
        }
    }

    private static FieldInfo[] Fields(object target) =>
        target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

    private static T Read<T>(object target) =>
        Assert.IsType<T>(Fields(target).Single(field => field.FieldType == typeof(T)).GetValue(target));
}
