using network.common.data;
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
        var player = new game_server.players.Player(new PlayerInfo { PlayerId = playerId, Name = "Participant" })
        {
            Health = 37
        };
        using (match.Enter())
        {
            match.RegisterPlayer(player);
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
            match.RegisterPlayer(winner);
            match.RegisterPlayer(other);
            match.RegisterPlayer(eliminated);
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
    public void RankingsBreakOrbTiesByHealthLikeTheTimeoutWinner()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947805);
        using (match.Enter())
        {
            var weaker = new game_server.players.Player(new PlayerInfo { PlayerId = 101 }) { Health = 40 };
            var stronger = new game_server.players.Player(new PlayerInfo { PlayerId = 102 }) { Health = 90 };
            match.RegisterPlayer(weaker);
            match.RegisterPlayer(stronger);
            Assert.True(TestGameSessionServices.Orbs(match, 101).TryAddOrbWithCapacity(107000010, 8, out _));
            Assert.True(TestGameSessionServices.Orbs(match, 102).TryAddOrbWithCapacity(107000010, 8, out _));

            // 오브 수와 티어가 같으면 체력이 높은 쪽이 화면 순위에서도 앞선다. ID는 101이 작지만 뒤로 간다.
            new MatchSynchronizationService().CollectOrbRankings(match,
                new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, new List<game_server.sessions.GameClientSession> { TestGameSessionServices.CreateRecipientSession() }));
            Assert.Equal("102:1|101:1", match.OrbRankingsSignature);

            // 시간 만료 때 승자를 정하는 비교도 같은 함수라 같은 플레이어가 이긴다.
            Assert.True(MatchResultService.CompareOrbScore((102, 1, 1, 90), (101, 1, 1, 40)) < 0);
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
                match.RegisterPlayer(new game_server.players.Player(new PlayerInfo { PlayerId = id }));
            for (int i = 0; i < 2; i++)
                Assert.True(TestGameSessionServices.Orbs(match, 101).TryAddOrbWithCapacity(107000010, 8, out _));
            Assert.True(TestGameSessionServices.Orbs(match, -102).TryAddOrbWithCapacity(107000010, 8, out _));
            for (int i = 0; i < 6; i++)
                Assert.True(TestGameSessionServices.Orbs(match, 103).TryAddOrbWithCapacity(107000010, 8, out _));
            match.TryEliminatePlayer(103, network.common.EliminationReason.HEALTH_ZERO);
            Assert.Empty(match.GetSessions());
            new MatchSynchronizationService().CollectOrbRankings(match,
                new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, new List<game_server.sessions.GameClientSession> { TestGameSessionServices.CreateRecipientSession() }));
            var signature = match.OrbRankingsSignature;
            Assert.Equal("101:2|-102:1|103:0", signature);
        }
    }

    [Fact]
    public void TailCutGuardBlocksEveryCutterUntilItExpires()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchTrailCutService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947803);
        var cutter = new game_server.players.Player(new PlayerInfo { PlayerId = 303 });
        var owner = new game_server.players.Player(new PlayerInfo { PlayerId = 404 });
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterPlayer(cutter);
            match.RegisterPlayer(owner);
            Assert.True(TestGameSessionServices.Orbs(match, owner.PlayerId).TryAddOrbWithCapacity(107000010, 1, out _));
            owner.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, network.common.AreaType.S2Gym1));
            owner.Position = TestMapPosition.In(network.common.AreaType.S2Gym1, 0, -1);
            var chains = new Dictionary<long, List<Vector3f>>
            {
                [owner.PlayerId] = [TestMapPosition.In(network.common.AreaType.S2Gym1)]
            };
            cutter.Position = TestMapPosition.In(network.common.AreaType.S2Gym1, 0.7f, 0.15f);
            cutter.TrailLastTickPosition = TestMapPosition.In(network.common.AreaType.S2Gym1, -0.7f, 0.15f);

            // 다른 누군가에게 방금 잘린 상대는 이 절단자도 자를 수 없다.
            owner.StatusEffects.Apply(PlayerStatusEffectKind.TailCutGuard, now.AddSeconds(1));
            service.ProcessTrailCut(match, cutter, chains, now);
            Assert.Single(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());

            // 보호가 끝나면 같은 돌진으로 잘린다.
            service.ProcessTrailCut(match, cutter, chains, now.AddSeconds(1));
            Assert.Empty(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
        }
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(-101, 100)]
    [InlineData(101, 35)]
    [InlineData(-101, 35)]
    public void TailCutCostsNoHealthForHumansAndBots(long cutterId, int health)
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
            match.RegisterPlayer(cutter);
            match.RegisterPlayer(owner);
            if (cutterId < 0) match.Bots.GetBots().Add(bot);
            Assert.True(TestGameSessionServices.Orbs(match, owner.PlayerId).TryAddOrbWithCapacity(107000010, 1, out _));
            Assert.Single(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
            owner.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(network.common.AreaType.S2Gym1)));
            owner.Position = TestMapPosition.In(network.common.AreaType.S2Gym1, 0, -1);
            var orbPointsByOwner = new Dictionary<long, List<Vector3f>>
            {
                [owner.PlayerId] = [TestMapPosition.In(network.common.AreaType.S2Gym1)]
            };
            cutter.Position = TestMapPosition.In(network.common.AreaType.S2Gym1, 0.7f, 0.15f);
            cutter.TrailLastTickPosition = TestMapPosition.In(network.common.AreaType.S2Gym1, -0.7f, 0.15f);
            service.ProcessTrailCut(match, cutter, orbPointsByOwner, now);

            // 절단은 체력을 깎지 않고, 체력이 낮아도 자를 수 있다.
            Assert.Equal(initialHealth, cutter.Health);
            Assert.Empty(TestGameSessionServices.Orbs(match, owner.PlayerId).GetAllOrbs());
            Assert.Equal(now.AddSeconds(network.common.Config.SWARM_TAIL_CUT_GUARD_SECONDS), owner.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.TailCutGuard));
        }
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void MonsterContactFindsPlayerWithoutSessionAndInterruptsPendingDoor(long playerId)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<game_server.matches.monsters.MonsterAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947801);
        var now = DateTime.UtcNow;
        var position = TestMapPosition.In(network.common.AreaType.S2Gym1);
        var player = new game_server.players.Player(new PlayerInfo { PlayerId = playerId })
        {
            Health = 100,
            Position = position
        };
        using (match.Enter())
        {
            match.RegisterPlayer(player);
            match.Monsters.Initialize(now);
            match.Monsters.Entities[1] = new game_server.matches.monsters.Monster { MonsterId = 1, Alive = true, Health = 10, Position = position, ContactDamageValue = 12 };
            player.Interactions.Begin(702000101, 0);

            // 몬스터와 겹쳐 선 플레이어는 세션이 없어도 물리고, 진행 중인 문 열기가 끊긴다.
            service.ProcessTick(match, [player], now);
            Assert.Equal(100 - network.common.Config.ScaleSwarmDamageTaken(12), player.Health);
            Assert.False(player.Interactions.TryComplete(702000101, 3000, TimeSpan.FromSeconds(3), out _));

            // 탈락한 플레이어는 공격 간격과 면역 창이 지나도 물리지 않는다.
            player.Status = network.common.PlayerMatchStatus.ELIMINATED;
            int health = player.Health;
            service.ProcessTick(match, [player], now.AddSeconds(10));
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
        var enemy = new game_server.players.Player(new PlayerInfo { PlayerId = enemyId })
        {
            Position = bot.Player.Position!
        };
        using (match.Enter())
        {
            match.RegisterPlayer(bot.Player);
            match.RegisterPlayer(enemy);
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
    public void WaveVortexDamagesAndSlowsPlayersWithoutSessionOrBotState()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var waveAttacks = provider.GetRequiredService<game_server.matches.MatchOrbAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947799);
        var now = DateTime.UtcNow;
        var human = new game_server.players.Player(new PlayerInfo { PlayerId = 11 })
        {
            Position = new Vector3f()
        };
        var bot = new game_server.players.Player(new PlayerInfo { PlayerId = -11 })
        {
            Position = new Vector3f()
        };
        using (match.Enter())
        {
            match.RegisterPlayer(human);
            match.RegisterPlayer(bot);
            match.PendingWaveAttacks.Add(new PendingWaveAttack(99, GameMapData.GetCurrentArea(human.GameInfo.ObjectInfo.MapId, human.GameInfo.ObjectInfo.Cell), new Vector3f(), 5, 2f, 107000030, now, true));
            waveAttacks.ProcessWaveAttacks(match, now);
            Assert.True(human.Health < network.common.Config.MAX_HEALTH);
            Assert.Equal(human.Health, bot.Health);
            Assert.Equal(now.AddSeconds(network.common.Config.SWARM_WAVE_SLOW_SECONDS), human.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow));
            Assert.Equal(human.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow), bot.StatusEffects.GetExpiresAt(PlayerStatusEffectKind.WaveSlow));
            Assert.Null(human.Session);
            Assert.Null(bot.Session);
        }
    }

    // 감속은 파도가 판의 과반(공명)일 때만 걸린다. 피해는 공명과 무관하다.
    [Fact]
    public void WaveVortexWithoutResonanceDamagesButDoesNotSlow()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var waveAttacks = provider.GetRequiredService<game_server.matches.MatchOrbAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(947798);
        var now = DateTime.UtcNow;
        var human = new game_server.players.Player(new PlayerInfo { PlayerId = 12 })
        {
            Position = new Vector3f()
        };
        using (match.Enter())
        {
            match.RegisterPlayer(human);
            match.PendingWaveAttacks.Add(new PendingWaveAttack(99, GameMapData.GetCurrentArea(human.GameInfo.ObjectInfo.MapId, human.GameInfo.ObjectInfo.Cell), new Vector3f(), 5, 2f, 107000030, now, false));
            waveAttacks.ProcessWaveAttacks(match, now);
            Assert.True(human.Health < network.common.Config.MAX_HEALTH);
            Assert.False(human.StatusEffects.IsActive(PlayerStatusEffectKind.WaveSlow, now));
        }
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
            combat.ProcessTick(match, DateTime.UtcNow);
        }
        Assert.Null(store.GetOrNull(match.MatchingId));
        using (match.Enter())
        {
            combat.ProcessTick(match, DateTime.UtcNow);
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
            match.RegisterPlayer(bot.Player);
            var now = DateTime.UtcNow;
            service.UpdateSleep(match, [bot], now);
            Assert.True(bot.Player.IsSleeping);

            match.GroundItems.SpawnItems(GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell), bot.Player.Position!.X, bot.Player.Position.Y, [network.common.Config.SUMMON_STONE_GROUND_ITEM_ID]);
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
        match.RegisterPlayer(bot.Player);
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
        match.RegisterPlayer(bot.Player);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            service.UpdateSleep(match, [bot], now);
            Assert.True(bot.Player.IsSleeping);
            match.RegisterPlayer(enemy.Player);
            service.UpdateSleep(match, [bot], now);
            Assert.False(bot.Player.IsSleeping);
            enemy.Player.Position = new Vector3f(1000, 1000, 0);
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
            var spawnCell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, GameMapData.GetCurrentArea(bot.Player.GameInfo.ObjectInfo.MapId, bot.Player.GameInfo.ObjectInfo.Cell));
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
}
