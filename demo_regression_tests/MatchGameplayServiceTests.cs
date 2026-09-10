using game_server.players.bots;
using game_server.combat;
using game_server.matches;
using System.Reflection;
using game_server;
using Microsoft.Extensions.DependencyInjection;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchGameplayServiceTests
{
    [Fact]
    public void Composition_CreatesCombatPerResolutionWithoutDependingBackOnHost()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var decisions = provider.GetRequiredService<BotDecisionService>();
        var combat = provider.GetRequiredService<MatchCombatService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        Assert.NotSame(combat, provider.GetRequiredService<MatchCombatService>());
        Assert.Same(decisions, Read<BotDecisionService>(combat));
        Assert.Same(store, Read<MatchRuntimeStore>(combat));
        Assert.Same(store, Read<MatchRuntimeStore>(decisions));
        Assert.DoesNotContain(Fields(combat), field => field.FieldType == typeof(GameServer));
        Assert.DoesNotContain(Fields(decisions), field =>
            field.FieldType == typeof(GameServer) || field.FieldType == typeof(MatchCombatService));
    }

    [Fact]
    public void Arena_SkipsMissingAndTerminalMatchesWithoutRecreatingThem()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var combat = provider.GetRequiredService<MatchCombatService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        combat.ProcessTick(947701, []);
        Assert.Null(store.GetOrNull(947701));
        var match = store.GetOrCreate(947702);
        using (MatchRuntimeStore.Enter(match))
        {
            match.TryMarkEnded();
            combat.ProcessTick(match.MatchingId, []);
        }
        Assert.Null(store.GetOrNull(match.MatchingId));
        combat.ProcessTick(match.MatchingId, []);
        Assert.Null(store.GetOrNull(match.MatchingId));
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
        using (MatchRuntimeStore.Enter(first))
        {
            var bot = new BotPlayerState { PlayerId = 11, Player = { Health = 10 } };
            first.BotTactics.LastDamagedAtUtc[(first.MatchingId, 11)] = now;
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(5));
            Assert.Equal(10, bot.Player.Health);
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Player.Health);
            service.ProcessSwarmBotRecovery(first.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Player.Health);
        }
        using (MatchRuntimeStore.Enter(second))
        {
            var bot = new BotPlayerState { PlayerId = 11, Player = { Health = 10 } };
            service.ProcessSwarmBotRecovery(second.MatchingId, [bot], now.AddSeconds(6));
            Assert.Equal(12, bot.Player.Health);
            second.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
    }

    [Fact]
    public void BotCut_UsesProvidedCostAndMatchCooldown()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947705);
        var now = DateTime.UtcNow;
        using (MatchRuntimeStore.Enter(match))
        {
            int half = (int)(network.common.Config.MAX_HEALTH * 0.5f);
            Assert.True(service.IsSwarmBotCutAllowed(match.MatchingId, 11, half + 5, now, 5));
            Assert.False(service.IsSwarmBotCutAllowed(match.MatchingId, 11, half + 5, now, 6));
            match.BotTactics.LastTrailCutAtUtc[(match.MatchingId, 11)] = now;
            Assert.False(service.IsSwarmBotCutAllowed(match.MatchingId, 11, network.common.Config.MAX_HEALTH, now.AddSeconds(5), 5));
            Assert.True(service.IsSwarmBotCutAllowed(match.MatchingId, 11, network.common.Config.MAX_HEALTH, now.AddSeconds(6), 5));
            match.TryMarkEnded();
        }
    }

    private static FieldInfo[] Fields(object target) =>
        target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

    private static T Read<T>(object target) =>
        Assert.IsType<T>(Fields(target).Single(field => field.FieldType == typeof(T)).GetValue(target));
}
