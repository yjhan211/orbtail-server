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
    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    public void BotSleepChecksParticipantsWithoutSessionsAndIgnoresEliminatedPlayers(long enemyId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947799);
        var bot = new BotPlayerState
        {
            PlayerId = -11,
            Player = { Health = 10, CurrentArea = network.common.Config.SWARM_MATCH_GROUND_AREA }
        };
        var enemy = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = enemyId },
            Position = new Vector3f(0, 0, 0),
            CurrentArea = bot.Player.CurrentArea
        };
        using (match.Enter())
        {
            match.RegisterParticipant(bot.Player);
            match.RegisterParticipant(enemy);
            Assert.Empty(match.GetSessions());
            var now = DateTime.UtcNow;
            service.UpdateSleep(match, [bot], now);
            Assert.False(bot.Player.IsSleeping);

            enemy.Status = network.common.PlayerMatchStatus.ELIMINATED;
            service.UpdateSleep(match, [bot], now);
            Assert.True(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void GrowthUsesPlayerRosterWithoutConnectionsAndIgnoresEliminatedPrey()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var growth = provider.GetRequiredService<MatchGrowthService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947798);
        var hunter = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = -1 }, CurrentArea = network.common.AreaType.S2Ground
        };
        var prey = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 1 }, CurrentArea = network.common.AreaType.S2Ground
        };
        using (match.Enter())
        {
            match.RegisterParticipant(hunter);
            match.RegisterParticipant(prey);
            Assert.True(match.Inventory.TryAddItemWithCapacity(hunter.PlayerId, 107000010, 6, out _));
            Assert.Equal(1, growth.GetTopOrbCount(match.MatchingId));
            Assert.False(growth.HasSwarmPreyInArea(match.MatchingId, hunter, [hunter]));
            Assert.True(growth.HasSwarmPreyInArea(match.MatchingId, hunter, [hunter, prey]));
            match.TryEliminatePlayer(prey.PlayerId, network.common.EliminationReason.HEALTH_ZERO);
            Assert.False(growth.HasSwarmPreyInArea(match.MatchingId, hunter, [hunter, prey]));
            match.TryEliminatePlayer(hunter.PlayerId, network.common.EliminationReason.HEALTH_ZERO);
            Assert.Equal(0, growth.GetTopOrbCount(match.MatchingId));
        }
    }

    [Fact]
    public void WaveVortexDamagesAndSlowsPlayersWithoutSessionOrBotState()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var combat = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947799);
        var now = DateTime.UtcNow;
        var human = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f(),
            CurrentArea = network.common.AreaType.S2Ground
        };
        var bot = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = -11 }, Position = new Vector3f(),
            CurrentArea = network.common.AreaType.S2Ground
        };
        using (match.Enter())
        {
            match.RegisterParticipant(human);
            match.RegisterParticipant(bot);
            combat.DetonateWaveOrbVortex(match.MatchingId, 99, human.CurrentArea,
                new Vector3f(), 5, 2f, 107000030, now, [human, bot], []);
            Assert.True(human.Health < network.common.Config.MAX_HEALTH);
            Assert.Equal(human.Health, bot.Health);
            Assert.Equal(now.AddSeconds(network.common.data.OrbData.WaveSlowSeconds), human.WaveSlowUntilUtc);
            Assert.Equal(human.WaveSlowUntilUtc, bot.WaveSlowUntilUtc);
            Assert.Null(human.Session);
            Assert.Null(bot.Session);
        }
    }

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
    public void BotSleepUsesSharedWarmupRecoveryAndCombatLock()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947703);
        var bot = new BotPlayerState { PlayerId = -11, Player = { Health = 10, CurrentArea = network.common.Config.SWARM_MATCH_GROUND_AREA } };
        match.RegisterParticipant(bot.Player);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            bot.Player.MarkSwarmCombat(now);
            service.UpdateSleep(match, [bot], now.AddSeconds(2));
            Assert.False(bot.Player.IsSleeping);
            MatchCombatService.ProcessSleepRecovery(match.MatchingId, [bot.Player], now.AddSeconds(2), store.EventLogs, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Equal(10, bot.Player.Health);
            service.UpdateSleep(match, [bot], now.AddSeconds(3));
            Assert.True(bot.Player.IsSleeping);
            MatchCombatService.ProcessSleepRecovery(match.MatchingId, [bot.Player], now.AddSeconds(3), store.EventLogs, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Equal(10, bot.Player.Health);
            MatchCombatService.ProcessSleepRecovery(match.MatchingId, [bot.Player], now.AddSeconds(4), store.EventLogs, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            int expected = 10 + Math.Max(1, (int)MathF.Round(network.common.Config.MAX_HEALTH * 0.05f));
            Assert.Equal(expected, bot.Player.Health);
            MatchCombatService.ProcessSleepRecovery(match.MatchingId, [bot.Player], now.AddSeconds(4), store.EventLogs, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.Equal(expected, bot.Player.Health);
            bot.Player.MarkSwarmCombat(now.AddSeconds(4));
            service.UpdateSleep(match, [bot], now.AddSeconds(4));
            Assert.False(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void BotSleepWakesForDangerAndFullHealthAndRespectsHealingLock()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotDecisionService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947704);
        var bot = new BotPlayerState { PlayerId = -11, Player = { Health = 10, CurrentArea = network.common.Config.SWARM_MATCH_GROUND_AREA } };
        var enemy = new BotPlayerState { PlayerId = -12 };
        match.RegisterParticipant(bot.Player);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            service.UpdateSleep(match, [bot], now);
            Assert.True(bot.Player.IsSleeping);
            match.RegisterParticipant(enemy.Player);
            service.UpdateSleep(match, [bot], now);
            Assert.False(bot.Player.IsSleeping);
            enemy.Player.Position = new Vector3f(1000, 1000, 0);
            bot.Player.BlockHealingUntil(now.AddSeconds(8));
            service.UpdateSleep(match, [bot], now.AddSeconds(7));
            Assert.False(bot.Player.IsSleeping);
            service.UpdateSleep(match, [bot], now.AddSeconds(8));
            Assert.True(bot.Player.IsSleeping);
            bot.Player.Recover(network.common.Config.MAX_HEALTH);
            service.UpdateSleep(match, [bot], now.AddSeconds(9));
            Assert.False(bot.Player.IsSleeping);
            bot.Player.ApplyDamage(1);
            bot.IsChannelHeld = true;
            service.UpdateSleep(match, [bot], now.AddSeconds(10));
            Assert.False(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void SleepingBotStopsWithoutPlanningWalkingOrPickingUpItems()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947706);
        var bot = new BotPlayerState { PlayerId = -11, Player = { Health = 10, CurrentArea = network.common.Config.SWARM_MATCH_GROUND_AREA, Velocity = new Vector3f(3, 0, 0) } };
        match.Bots.GetBots(match.MatchingId).Add(bot);
        using (match.Enter())
        {
            Assert.True(bot.Player.TryStartSleep(DateTime.UtcNow));
            var result = match.Bots.ProcessBotMovementTick(match.MatchingId, match.Closures,
                new Dictionary<long, network.common.AreaType>(), match.Inventory, match.GroundItems, [],
                (_, _) => throw new InvalidOperationException("A sleeping bot must not request a movement plan."), match.SummonStones);
            Assert.Empty(result.GroundItemPickups);
            Assert.Single(result.Movements);
            Assert.Equal(0, bot.Player.Velocity.X);
            Assert.Equal(0, bot.Player.Position!.X);
            Assert.True(bot.Player.IsSleeping);
            Assert.Equal(network.common.PlayerState.SLEEP, match.Bots.SynthesizeGameObjectInfo(match.MatchingId, bot.PlayerId)!.State);
            Assert.Equal(network.common.PlayerState.SLEEP, match.Bots.CreatePlayerInfo(match.MatchingId, bot.PlayerId)!.State);
        }
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
