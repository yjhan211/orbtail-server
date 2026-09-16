using System.Reflection;
using game_server;
using game_server.matches;
using game_server.players;
using game_server.players.bots;
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
        var service = provider.GetRequiredService<game_server.matches.MatchResultService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947804);
        var player = new game_server.players.Player(new PlayerInfo { PlayerId = playerId, Name = "Participant" }) {
            Health = 37
        };
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            Assert.Empty(match.GetSessions());
            Assert.Null(match.Bots.GetBot(playerId));
            var result = Assert.Single(service.BuildPlayerResults(match, playerId));
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
        var service = provider.GetRequiredService<MatchResultService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947803);
        var winner = new game_server.players.Player(new PlayerInfo { PlayerId = winnerId });
        var other = new game_server.players.Player(new PlayerInfo { PlayerId = 202 });
        var eliminated = new game_server.players.Player(new PlayerInfo { PlayerId = 303 });
        using (match.Enter())
        {
            match.RegisterParticipant(winner);
            match.RegisterParticipant(other);
            match.RegisterParticipant(eliminated);
            match.TryEliminatePlayer(303, network.common.EliminationReason.HEALTH_ZERO);
            for (int i = 0; i < 2; i++)
                Assert.True(TestGameSessionServices.Orbs(match, winnerId).TryAddOrbWithCapacity(107000010, 8, out _));
            for (int i = 0; i < 6; i++)
                Assert.True(TestGameSessionServices.Orbs(match, 303).TryAddOrbWithCapacity(107000010, 8, out _));
            match.StartGameplay();
            var deadline = match.StartsAtUtc!.Value.AddSeconds(network.common.Config.SWARM_MATCH_DURATION_SECONDS);
            Assert.Empty(match.GetSessions());
            Assert.False(service.TryEndOnScoreTimeout(match, deadline.AddMilliseconds(-1)));
            Assert.True(service.TryEndOnScoreTimeout(match, deadline));
            Assert.True(match.IsEnded);
            Assert.False(service.TryEndOnScoreTimeout(match, deadline.AddSeconds(1)));
        }
    }

    [Fact]
    public void RankingsUseAllPlayersAndGiveEliminatedPlayersZeroPoints()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchResultService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947804);
        using (match.Enter())
        {
            foreach (long id in new long[] { 101, -102, 103 })
                match.RegisterParticipant(new game_server.players.Player(new PlayerInfo { PlayerId = id }));
            for (int i = 0; i < 2; i++)
                Assert.True(TestGameSessionServices.Orbs(match, 101).TryAddOrbWithCapacity(107000010, 8, out _));
            Assert.True(TestGameSessionServices.Orbs(match, -102).TryAddOrbWithCapacity(107000010, 8, out _));
            for (int i = 0; i < 6; i++)
                Assert.True(TestGameSessionServices.Orbs(match, 103).TryAddOrbWithCapacity(107000010, 8, out _));
            match.TryEliminatePlayer(103, network.common.EliminationReason.HEALTH_ZERO);
            Assert.Empty(match.GetSessions());
            service.BroadcastOrbRankings(match,
                new List<game_server.sessions.GameClientSession> { TestGameSessionServices.CreateRecipientSession() });
            var signature = match.OrbRankingsSignature;
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
        var service = provider.GetRequiredService<MatchTrailCutService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947802);
        int initialHealth = health == 35 ? health : network.common.Config.MAX_HEALTH;
        var bot = new Bot { PlayerId = cutterId, Player = { Health = initialHealth } };
        var cutter = bot.Player;
        var owner = new game_server.players.Player(new PlayerInfo { PlayerId = 202 });
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(cutter);
            match.RegisterParticipant(owner);
            if (cutterId < 0) match.Bots.GetBots().Add(bot);
            Assert.True(TestGameSessionServices.Orbs(match, owner.PlayerId).TryAddOrbWithCapacity(107000010, 1, out _));
            Assert.Single(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
            owner.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Gym1)));
            owner.Position = TestMapPosition.In(network.common.AreaType.S2Gym1, 0, -1);
            var orbPointsByOwner = new Dictionary<long, List<Vector3f>> { [owner.PlayerId] = [TestMapPosition.In(network.common.AreaType.S2Gym1)] };
            service.TryPerformSwarmTrailCut(match, cutterId, network.common.AreaType.S2Gym1,
                TestMapPosition.In(network.common.AreaType.S2Gym1, -0.7f, 0.15f), TestMapPosition.In(network.common.AreaType.S2Gym1, 0.7f, 0.15f), orbPointsByOwner, now,
                new List<game_server.sessions.GameClientSession>());

            if (health == 35)
            {
                Assert.Equal(health, cutter.Health);
                Assert.Single(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
                return;
            }
            Assert.Equal(initialHealth - 35, cutter.Health);
            Assert.Empty(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
            Assert.True(cutter.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.HealingBlocked) >= now.AddSeconds(8));
            if (cutterId < 0)
            {
                Assert.Equal(now, bot.LastTrailCutAtUtc);
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
        var player = new game_server.players.Player(new PlayerInfo { PlayerId = playerId }) { Health = 100
        };
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            player.Interactions.Begin(702000101, 0);
            service.ApplySwarmParticipantDamage(match,
                new MonsterContactDamage(1, playerId, player.GameInfo.ObjectInfo.Area, 12), []);
            Assert.Equal(100 - network.common.Config.ScaleSwarmDamageTaken(12), player.Health);
            Assert.False(player.Interactions.TryComplete(702000101, 3000, TimeSpan.FromSeconds(3), out _));

            player.Status = network.common.PlayerMatchStatus.ELIMINATED;
            int health = player.Health;
            service.ApplySwarmParticipantDamage(match,
                new MonsterContactDamage(1, playerId, player.GameInfo.ObjectInfo.Area, 12), []);
            Assert.Equal(health, player.Health);
        }
    }


    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    public void BotSleepChecksParticipantsWithoutSessionsAndIgnoresEliminatedPlayers(long enemyId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotBehaviorService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947799);
        var bot = new Bot
        {
            PlayerId = -11,
            Player = { Health = 10, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Corridor9)) }
        };
        var enemy = new game_server.players.Player(new PlayerInfo { PlayerId = enemyId }) {
            Position = bot.Player.Position!
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
        var growth = provider.GetRequiredService<PlayerOrbGrowthService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947798);
        var hunter = new game_server.players.Player(new PlayerInfo { PlayerId = -1 }) { Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Gym1))
        };
        var prey = new game_server.players.Player(new PlayerInfo { PlayerId = 1 }) { Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Gym1))
        };
        using (match.Enter())
        {
            match.RegisterParticipant(hunter);
            match.RegisterParticipant(prey);
            Assert.True(TestGameSessionServices.Orbs(match, hunter.PlayerId).TryAddOrbWithCapacity(107000010, 6, out _));
            Assert.Equal(1, growth.GetTopOrbCount(match));
            match.TryEliminatePlayer(prey.PlayerId, network.common.EliminationReason.HEALTH_ZERO);
            match.TryEliminatePlayer(hunter.PlayerId, network.common.EliminationReason.HEALTH_ZERO);
            Assert.Equal(0, growth.GetTopOrbCount(match));
        }
    }

    [Fact]
    public void WaveVortexDamagesAndSlowsPlayersWithoutSessionOrBotState()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var waveAttacks = provider.GetRequiredService<game_server.matches.MatchOrbAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947799);
        var now = DateTime.UtcNow;
        var human = new game_server.players.Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f()
        };
        var bot = new game_server.players.Player(new PlayerInfo { PlayerId = -11 }) { Position = new Vector3f()
        };
        using (match.Enter())
        {
            match.RegisterParticipant(human);
            match.RegisterParticipant(bot);
            match.PendingWaveAttacks.Add(new PendingWaveAttack(99, human.GameInfo.ObjectInfo.Area, new Vector3f(), 5, 2f, 107000030, now));
            waveAttacks.ProcessWaveDetonations(match, now);
            Assert.True(human.Health < network.common.Config.MAX_HEALTH);
            Assert.Equal(human.Health, bot.Health);
            Assert.Equal(now.AddSeconds(network.common.data.OrbData.WaveSlowSeconds), human.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow));
            Assert.Equal(human.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow), bot.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow));
            Assert.Null(human.Session);
            Assert.Null(bot.Session);
        }
    }

    [Fact]
    public void Composition_SharesOneCombatServiceWithoutDependingBackOnHost()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var decisions = provider.GetRequiredService<BotBehaviorService>();
        var combat = provider.GetRequiredService<MatchCombatService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        Assert.Same(combat, provider.GetRequiredService<MatchCombatService>());
        Assert.Same(decisions, Read<BotBehaviorService>(combat));
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
        using (match.Enter())
        {
            combat.ProcessTick(match);
        }
        Assert.Null(store.GetOrNull(match.MatchingId));
    }

    [Fact]
    public void BotWakesForSummonStoneAndSleepsAgainAfterItDisappears()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotBehaviorService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947705);
        var bot = new Bot { PlayerId = -11, Player = { Health = 10, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Corridor9)) } };
        using (match.Enter())
        {
            match.RegisterParticipant(bot.Player);
            var now = DateTime.UtcNow;
            service.UpdateSleep(match, [bot], now);
            Assert.True(bot.Player.IsSleeping);

            match.GroundItems.SpawnItems(bot.Player.GameInfo.ObjectInfo.Area, bot.Player.Position!.X, bot.Player.Position.Y, [network.common.Config.SUMMON_STONE_GROUND_ITEM_ID]);
            service.UpdateSleep(match, [bot], now);
            Assert.False(bot.Player.IsSleeping);
            service.UpdateSleep(match, [bot], now.AddSeconds(1));
            Assert.False(bot.Player.IsSleeping);

            match.GroundItems.Release();
            service.UpdateSleep(match, [bot], now.AddSeconds(2));
            Assert.True(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void BotSleepUsesSharedWarmupRecovery()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotBehaviorService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947703);
        var bot = new Bot { PlayerId = -11, Player = { Health = 10, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Corridor9)) } };
        match.RegisterParticipant(bot.Player);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            service.UpdateSleep(match, [bot], now.AddSeconds(3));
            Assert.True(bot.Player.IsSleeping);
            TestGameSessionServices.CreateHealthService(store).ApplySleepRecovery(match, [bot.Player], now.AddSeconds(3));
            Assert.Equal(10, bot.Player.Health);
            TestGameSessionServices.CreateHealthService(store).ApplySleepRecovery(match, [bot.Player], now.AddSeconds(4));
            int expected = 10 + Math.Max(1, (int)MathF.Round(network.common.Config.MAX_HEALTH * 0.05f));
            Assert.Equal(expected, bot.Player.Health);
            TestGameSessionServices.CreateHealthService(store).ApplySleepRecovery(match, [bot.Player], now.AddSeconds(4));
            Assert.Equal(expected, bot.Player.Health);
            service.UpdateSleep(match, [bot], now.AddSeconds(4));
            Assert.True(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void BotSleepWakesForDangerAndFullHealthButIgnoresHealingLock()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotBehaviorService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947704);
        var bot = new Bot { PlayerId = -11, Player = { Health = 10, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Corridor9)) } };
        var enemy = new Bot { PlayerId = -12 };
        enemy.Player.Position = bot.Player.Position!;
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
            bot.Player.StatusEffects.Apply(PlayerStatusEffectKind.HealingBlocked, now.AddSeconds(8));
            service.UpdateSleep(match, [bot], now.AddSeconds(7));
            Assert.True(bot.Player.IsSleeping);
            service.UpdateSleep(match, [bot], now.AddSeconds(8));
            Assert.True(bot.Player.IsSleeping);
            bot.Player.Recover(network.common.Config.MAX_HEALTH);
            service.UpdateSleep(match, [bot], now.AddSeconds(9));
            Assert.False(bot.Player.IsSleeping);
            bot.Player.ApplyDamage(1);
            bot.Player.Interactions.Begin(10, 0);
            service.UpdateSleep(match, [bot], now.AddSeconds(10));
            Assert.False(bot.Player.IsSleeping);
        }
    }

    [Fact]
    public void SleepingBotStopsWithoutPlanningWalkingOrPickingUpItems()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947706);
        var bot = new Bot { PlayerId = -11, Player = { Health = 10, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Corridor9)), Velocity = new Vector3f(3, 0, 0) } };
        match.Bots.GetBots().Add(bot);
        using (match.Enter())
        {
            var spawnCell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, bot.Player.GameInfo.ObjectInfo.Area);
            bot.Player.Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, spawnCell);
            var originalPosition = bot.Player.Position;
            Assert.True(bot.Player.TryStartSleep());
            var result = MovementTickTestDriver.RunBotTick(match,
                _ => throw new InvalidOperationException("A sleeping bot must not request a movement plan."));
            Assert.Single(result.Movements);
            Assert.Equal(0, bot.Player.Velocity.X);
            Assert.Equal(originalPosition, bot.Player.Position);
            Assert.True(bot.Player.IsSleeping);
            Assert.Equal(network.common.PlayerState.SLEEP, match.Bots.GetPlayerObjectInfo(bot.PlayerId)!.State);
        }
    }
    [Fact]
    public void BotCut_UsesProvidedCostAndMatchCooldown()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<BotBehaviorService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var match = store.GetOrCreate(947705);
        var now = DateTime.UtcNow;
        var bot = new Bot { PlayerId = 11 };
        using (MatchRuntimeStore.Enter(match))
        {
            int half = (int)(network.common.Config.MAX_HEALTH * 0.5f);
            Assert.True(service.CanCutTrail(bot, half + 5, now, 5));
            Assert.False(service.CanCutTrail(bot, half + 5, now, 6));
            bot.LastTrailCutAtUtc = now;
            Assert.False(service.CanCutTrail(bot, network.common.Config.MAX_HEALTH, now.AddSeconds(5), 5));
            Assert.True(service.CanCutTrail(bot, network.common.Config.MAX_HEALTH, now.AddSeconds(6), 5));
            match.TryMarkEnded();
        }
    }

    private static FieldInfo[] Fields(object target) =>
        target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

    private static T Read<T>(object target) =>
        Assert.IsType<T>(Fields(target).Single(field => field.FieldType == typeof(T)).GetValue(target));
}
