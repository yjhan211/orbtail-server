using game_server.players;
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
    [InlineData(101)]
    [InlineData(-101)]
    public void ResultsReadHealthAndProfileFromParticipantWithoutSession(long playerId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<game_server.matches.results.MatchResultService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947804);
        var player = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = playerId, Name = "Participant" },
            Health = 37
        };
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            Assert.Empty(match.GetSessions());
            Assert.Null(match.Bots.GetBot(match.MatchingId, playerId));
            var result = Assert.Single(service.BuildPlayerResults(match.MatchingId, playerId));
            Assert.Equal(playerId, result.PlayerId);
            Assert.Equal("Participant", result.Name);
            Assert.Equal(37, result.Health);
            Assert.Equal(1, result.Rank);
        }
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void ScoreTimeoutIncludesDisconnectedPlayersAndFinalizesOnlyOnce(long winnerId)
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947803);
        var winner = new game_server.players.Player { Profile = new PlayerInfo { PlayerId = winnerId } };
        var other = new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 202 } };
        var eliminated = new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 303 } };
        using (match.Enter())
        {
            match.RegisterParticipant(winner);
            match.RegisterParticipant(other);
            match.RegisterParticipant(eliminated);
            match.TryEliminatePlayer(303, network.common.EliminationReason.HEALTH_ZERO);
            for (int i = 0; i < 2; i++)
                Assert.True(match.Inventory.TryAddItemWithCapacity(winnerId, 107000010, 8, out _));
            for (int i = 0; i < 6; i++)
                Assert.True(match.Inventory.TryAddItemWithCapacity(303, 107000010, 8, out _));
            match.StartGameplay();
            var deadline = match.StartsAtUtc!.Value.AddSeconds(network.common.Config.SWARM_MATCH_DURATION_SECONDS);
            Assert.Empty(match.GetSessions());
            Assert.False(service.ProcessSwarmScoreTimeout(match.MatchingId, deadline.AddMilliseconds(-1)));
            Assert.True(service.ProcessSwarmScoreTimeout(match.MatchingId, deadline));
            Assert.True(match.IsEnded);
            Assert.False(service.ProcessSwarmScoreTimeout(match.MatchingId, deadline.AddSeconds(1)));
            var events = provider.GetRequiredService<game_server.logging.GameEventLogManager>().GetForPersistence(match.MatchingId);
            var result = Assert.Single(events.Where(entry => entry.Type == game_server.logging.GameEventType.MatchEnded));
            Assert.Equal(winnerId, result.WinnerPlayerId);
        }
    }

    [Fact]
    public void RankingsUseAllPlayersAndGiveEliminatedPlayersZeroPoints()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947804);
        using (match.Enter())
        {
            foreach (long id in new long[] { 101, -102, 103 })
                match.RegisterParticipant(new game_server.players.Player { Profile = new PlayerInfo { PlayerId = id } });
            for (int i = 0; i < 2; i++)
                Assert.True(match.Inventory.TryAddItemWithCapacity(101, 107000010, 8, out _));
            Assert.True(match.Inventory.TryAddItemWithCapacity(-102, 107000010, 8, out _));
            for (int i = 0; i < 6; i++)
                Assert.True(match.Inventory.TryAddItemWithCapacity(103, 107000010, 8, out _));
            match.TryEliminatePlayer(103, network.common.EliminationReason.HEALTH_ZERO);
            Assert.Empty(match.GetSessions());
            var broadcast = typeof(MatchCombatService).GetMethod("BroadcastSwarmOrbRankings",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            broadcast.Invoke(service, [match.MatchingId,
                new List<game_server.sessions.GameClientSession> { TestGameSessionServices.CreateRecipientSession() }]);
            var signature = typeof(MatchCombatService).GetField("OrbRankingsSignature",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
            Assert.Equal("101:2|-102:1|103:0", signature);
        }
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(-101, 100)]
    [InlineData(101, 35)]
    [InlineData(-101, 35)]
    public void TailCutUsesPlayerHealthWithoutConnection(long cutterId, int health)
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947802);
        int initialHealth = health == 35 ? health : network.common.Config.MAX_HEALTH;
        var bot = new BotPlayerState { PlayerId = cutterId, Player = { Health = initialHealth } };
        var cutter = bot.Player;
        var owner = new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 202 } };
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(cutter);
            match.RegisterParticipant(owner);
            if (cutterId < 0) match.Bots.GetBots(match.MatchingId).Add(bot);
            Assert.True(match.Inventory.TryAddItemWithCapacity(owner.PlayerId, 107000010, 1, out _));
            var orb = Assert.Single(match.Inventory.GetPlayerInventory(owner.PlayerId).GetAllItems());
            var chains = new Dictionary<long, (network.common.AreaType Area, Vector3f OwnerPosition,
                List<Vector3f> Points, List<long> Uids, List<int> ItemIds)>
            {
                [owner.PlayerId] = (network.common.AreaType.S2Ground, new Vector3f(0, -1, 0),
                    [new Vector3f(0, 0, 0)], [orb.ItemUid], [orb.ItemId])
            };
            var cut = typeof(MatchCombatService).GetMethod("TryPerformSwarmTrailCut",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            cut.Invoke(service, [match.MatchingId, cutterId, cutterId, network.common.AreaType.S2Ground,
                new Vector3f(-0.7f, 0.15f, 0), new Vector3f(0.7f, 0.15f, 0), chains, now,
                new List<game_server.sessions.GameClientSession>()]);

            if (health == 35)
            {
                Assert.Equal(health, cutter.Health);
                Assert.Single(match.Inventory.GetPlayerInventory(owner.PlayerId).GetAllItems());
                return;
            }
            Assert.Equal(initialHealth - 35, cutter.Health);
            Assert.Empty(match.Inventory.GetPlayerInventory(owner.PlayerId).GetAllItems());
            Assert.True(cutter.HealLockUntilUtc >= now.AddSeconds(8));
            if (cutterId < 0)
            {
                Assert.Equal(now, match.BotTactics.LastTrailCutAtUtc[(match.MatchingId, cutterId)]);
                Assert.True(bot.LastDamagedAtUtc >= now);
            }
        }
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void MonsterContactFindsPlayerWithoutSessionAndInterruptsPendingDoor(long playerId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947801);
        var player = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = playerId }, Health = 100
        };
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            player.CompleteDoor();
            player.BeginDoor(702000101, 0);
            service.ApplySwarmParticipantDamage(match.MatchingId,
                new SwarmPlayerDamage(1, playerId, player.CurrentArea, 12), []);
            Assert.Equal(100 - network.common.Config.ScaleSwarmDamageTaken(12), player.Health);
            Assert.False(player.TryFinishDoor(702000101, 3000, TimeSpan.FromSeconds(3), out _));

            player.Status = network.common.PlayerMatchStatus.ELIMINATED;
            int health = player.Health;
            service.ApplySwarmParticipantDamage(match.MatchingId,
                new SwarmPlayerDamage(1, playerId, player.CurrentArea, 12), []);
            Assert.Equal(health, player.Health);
        }
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void PeriodicBuffsHealAndEliminateParticipantsWithoutConnections(long playerId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchCombatService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947800);
        var player = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = playerId }, Health = 10
        };
        var survivor = new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 202 } };
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            match.RegisterParticipant(survivor);
            Assert.Empty(match.GetSessions());
            player.AddPeriodicBuff(network.common.BuffSubType.HEALTH_ADD, 3, 1, 1, now);
            service.ProcessPeriodicBuffs(match, [player, survivor], now.AddMilliseconds(999));
            Assert.Equal(10, player.Health);
            service.ProcessPeriodicBuffs(match, [player, survivor], now.AddSeconds(1));
            Assert.Equal(13, player.Health);
            Assert.Equal(3,
                provider.GetRequiredService<game_server.logging.GameEventLogManager>()
                    .GetResultStats(match.MatchingId, playerId).TotalRecovery);

            player.AddPeriodicBuff(network.common.BuffSubType.HEALTH_DOWN, 20, 1, 10, now.AddSeconds(1));
            service.ProcessPeriodicBuffs(match, [player, survivor], now.AddSeconds(2));
            Assert.Equal(0, player.Health);
            Assert.True(player.IsEliminated);
            Assert.Equal(0, TestGameSessionServices.GetPeriodicBuffCount(player));
            Assert.True(match.IsEnded);
        }
    }

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
    public void GrowthRankingUsesAlivePlayersWithoutConnections()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var growth = provider.GetRequiredService<GrowthService>();
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
            match.TryEliminatePlayer(prey.PlayerId, network.common.EliminationReason.HEALTH_ZERO);
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
    public void Arena_SkipsTerminalAndRemovedRuntimeWithoutRecreatingIt()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var combat = provider.GetRequiredService<MatchCombatService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947702);
        using (MatchRuntimeStore.Enter(match))
        {
            match.TryMarkEnded();
            combat.ProcessTick(match);
        }
        Assert.Null(store.GetOrNull(match.MatchingId));
        combat.ProcessTick(match);
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
            bot.Player.BeginDoor(10, 0);
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
            Assert.Single(result.Movements);
            Assert.Equal(0, bot.Player.Velocity.X);
            Assert.Equal(0, bot.Player.Position!.X);
            Assert.True(bot.Player.IsSleeping);
            Assert.Equal(network.common.PlayerState.SLEEP, match.Bots.SynthesizeGameObjectInfo(match.MatchingId, bot.PlayerId)!.State);
            Assert.Equal(network.common.PlayerState.NONE, match.Bots.GetPlayerProfile(match.MatchingId, bot.PlayerId)!.State);
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
