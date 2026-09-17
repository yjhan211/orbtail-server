using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOrbAttackServiceTests
{
    public MatchOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(-1, 0, 3)]
    [InlineData(0, 1, 3)]
    [InlineData(0, -1, 1)]
    public void CellShapeUsesEndpointCenters(int dx, int dy, int lengthCells)
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1);
        var shape = new PendingSunAttack
        {
            OriginCell = cell, EndCell = new Cell(cell.X + dx * lengthCells, cell.Y + dy * lengthCells)
        };
        var origin = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var next = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, new Cell(cell.X + dx, cell.Y + dy));

        Assert.Equal(origin, shape.Origin);
        Assert.Equal(origin.X + (next.X - origin.X) * lengthCells, shape.End.X);
        Assert.Equal(origin.Y + (next.Y - origin.Y) * lengthCells, shape.End.Y);
        Assert.InRange(MathF.Abs(shape.GroundLength - GroundGeometry.GroundDistance(origin, next) * lengthCells), 0f, 0.0001f);
    }

    [Fact]
    public void EntryPointsRequireMatchLockAndEndedMatchDoesNotAdvanceBurns()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947604);
        var service = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store)), new PlayerOrbService(), new MatchSynchronizationService());
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessSunAttacks(match, now));
        Assert.Throws<InvalidOperationException>(() => service.ProcessSunBurns(match, now));
        using (match.Enter())
        {
            var victim = new Player(new PlayerInfo { PlayerId = 12 });
            match.RegisterPlayer(victim);
            victim.StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(11, 107000010, AreaType.None, now.AddSeconds(5), now.AddSeconds(1)));
            var before = victim.StatusEffects.SunBurn;
            match.TryMarkEnded();
            service.ProcessSunBurns(match, now.AddSeconds(10));
            Assert.Equal(before, victim.StatusEffects.SunBurn);
        }
    }

    [Fact]
    public void Process_WaitsForTelegraphHitsOnceAndRemovesExpiredShape()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947601);
        var service = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)), new PlayerOrbService(), new MatchSynchronizationService());
        var now = DateTime.UtcNow;
        var owner = new Bot { PlayerId = 11 };
        var victim = new Bot { PlayerId = 12 };
        match.RegisterPlayer(owner.Player);
        match.RegisterPlayer(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            var originCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, TestMapPosition.In(AreaType.S2Gym1));
            var shape = new PendingSunAttack
            {
                EventId = 1, OwnerId = 11, WeaponItemId = 107000010, Damage = 10,
                Area = AreaType.S2Gym1,
                OriginCell = originCell,
                EndCell = new Cell(originCell.X + 8, originCell.Y),
                ArmedAtUtc = now.AddSeconds(1), ExpiresAtUtc = now.AddSeconds(3)
            };
            match.PendingSunAttacks.Add(shape);
            owner.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
            var hitCell = new Cell(shape.OriginCell.X + 2, shape.OriginCell.Y);
            owner.Player.Position = victim.Player.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, hitCell);
            service.ProcessSunAttacks(match, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.Empty(shape.HitVictims);

            var hitAt = shape.ArmedAtUtc.AddSeconds(3f / Config.SWARM_SUN_SWEEP_SPEED);
            service.ProcessSunAttacks(match, hitAt);
            int healthAfterHit = victim.Player.Health;
            Assert.InRange(healthAfterHit, 1, Config.MAX_HEALTH - 1);
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);
            Assert.Equal(12L, Assert.Single(shape.HitVictims));
            Assert.Equal(hitAt.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS), victim.Player.StatusEffects.SunBurn!.NextTickAtUtc);

            service.ProcessSunAttacks(match, hitAt.AddSeconds(0.1));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            service.ProcessSunAttacks(match, now.AddSeconds(3));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            Assert.Empty(match.PendingSunAttacks);
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void BurnTick_AppliesOnlyToOwningMatchAndDoesNotRepeatAtSameTime()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947602);
        var second = store.GetOrCreate(947603);
        var service = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)), new PlayerOrbService(), new MatchSynchronizationService());
        var now = DateTime.UtcNow;
        var burned = new Bot { PlayerId = 12 };
        using (MatchRuntimeStore.Enter(first))
        {
            first.RegisterPlayer(burned.Player);
            burned.Player.StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(11, 107000010, AreaType.S2Gym1,
                now.AddSeconds(Config.SWARM_SUN_BURN_SECONDS), now.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS)));
        }
        var due = now.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
        using (MatchRuntimeStore.Enter(second))
        {
            var victim = new Bot { PlayerId = 12 };
            second.RegisterPlayer(victim.Player);
            service.ProcessSunBurns(second, due);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            second.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(first))
        {
            var victim = burned;
            service.ProcessSunBurns(first, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            service.ProcessSunBurns(first, due);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_ORB_SHOCK_DAMAGE) *
                Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            service.ProcessSunBurns(first, due);
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            first.TryMarkEnded();
        }
    }

    private static readonly DateTime NowUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SunBurn_TicksOnInclusiveDueBoundaryRefreshesPayloadAndExpiresAfterLastTick()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(43002);
        var service = CreateService(store);
        var victim = new Bot { PlayerId = 20 };
        using var scope = MatchRuntimeStore.Enter(match);
        match.RegisterPlayer(victim.Player);
        int tickDamage = Math.Max(1, (int)MathF.Round(
            Config.ScaleSwarmDamageTaken(Config.SWARM_ORB_SHOCK_DAMAGE) * Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
        double interval = Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS;

        victim.Player.StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(10, 107000010, AreaType.S2Gym1, NowUtc.AddSeconds(interval * 3), NowUtc.AddSeconds(interval)));

        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval).AddMilliseconds(-1));
        Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);

        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        Assert.Equal(NowUtc.AddSeconds(interval * 2), victim.Player.StatusEffects.SunBurn!.NextTickAtUtc);

        // 재피격은 지속·다음 틱을 새로 잡는다.
        DateTime refreshedAtUtc = NowUtc.AddSeconds(interval * 1.5);
        victim.Player.StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(11, 107000011, AreaType.S2Gym1, refreshedAtUtc.AddSeconds(interval * 3), refreshedAtUtc.AddSeconds(interval)));
        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval * 2));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage * 2, victim.Player.Health);

        // 만료 시각에 걸린 마지막 틱은 적용한 뒤 화상을 걷는다.
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval * 2));
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval * 3));
        Assert.Equal(Config.MAX_HEALTH - tickDamage * 4, victim.Player.Health);
        Assert.Null(victim.Player.StatusEffects.SunBurn);
    }
    private static MatchOrbAttackService CreateService(MatchRuntimeStore store) =>
        new(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store)), new PlayerOrbService(), new MatchSynchronizationService());

    [Fact]
    public void WaveWaitsForTargetAndFuseThenDetonatesOnce()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchOrbAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948501);
        var trails = provider.GetRequiredService<PlayerOrbTrailService>();
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var victim = new Player(new PlayerInfo { PlayerId = -11 }) { Position = new Vector3f(100, 100, 0) };
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterPlayer(owner);
            match.RegisterPlayer(victim);
            var orb = owner.Orbs.AddOrb(107000030);
            attacks.ActivateOrbs(match, owner, now);
            Assert.Empty(match.PendingWaveAttacks);
            long key = orb.ItemUid;
            var readyAt = owner.Orbs.GetNextOrbAttackAtUtc(key)!.Value;
            Assert.True(readyAt > now);
            attacks.ActivateOrbs(match, owner, readyAt);
            Assert.Empty(match.PendingWaveAttacks);
            Assert.Equal(readyAt, owner.Orbs.GetNextOrbAttackAtUtc(key)!.Value);
            victim.Position = PlayerOrbTrailService.GetOrbPosition(match, owner, 0, owner.Position, PlayerOrbTrailService.GetOrbTiersInOrder(match, owner));
            attacks.ActivateOrbs(match, owner, readyAt);
            var pending = Assert.Single(match.PendingWaveAttacks);
            Assert.True(pending.ExplodeAtUtc > readyAt);
            owner.Orbs.TakeAllOrbs();
            owner.Status = PlayerMatchStatus.ELIMINATED;
            service.ProcessWaveAttacks(match, pending.ExplodeAtUtc.AddTicks(-1));
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            service.ProcessWaveAttacks(match, pending.ExplodeAtUtc);
            Assert.Empty(match.PendingWaveAttacks);
            Assert.True(victim.Health < Config.MAX_HEALTH);
            int health = victim.Health;
            service.ProcessWaveAttacks(match, pending.ExplodeAtUtc);
            Assert.Equal(health, victim.Health);
        }
    }

    [Fact]
    public void StateIsMatchOwnedAndTickRequiresLockAndSkipsEndedMatch()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchOrbAttackService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var first = store.GetOrCreate(948502);
        var second = store.GetOrCreate(948503);
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessWaveAttacks(first, now));
        using (first.Enter())
        {
            first.PendingWaveAttacks.Add(new PendingWaveAttack(1, AreaType.None, new Vector3f(), 1, 1, 107000030, now, true));
            first.TryMarkEnded();
            service.ProcessWaveAttacks(first, now);
            Assert.Single(first.PendingWaveAttacks);
        }
        using (second.Enter())
        {
            service.ProcessWaveAttacks(second, now);
            Assert.Empty(second.PendingWaveAttacks);
        }
    }

    [Fact]
    public void PlayerActivationRequiresLockAndOwnMatchAndKeepsClocksOnPlayer()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948504);
        var first = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var second = new Player(new PlayerInfo { PlayerId = 12 }) { Position = new Vector3f() };
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => attacks.ActivateOrbs(match, first, now));
        using (match.Enter())
        {
            Assert.Throws<InvalidOperationException>(() => attacks.ActivateOrbs(match, first, now));
            match.RegisterPlayer(first);
            match.RegisterPlayer(second);
            var orb = first.Orbs.AddOrb(107000030);
            attacks.ActivateOrbs(match, first, now);
            Assert.NotNull(first.Orbs.GetNextOrbAttackAtUtc(orb.ItemUid));
            Assert.Null(second.Orbs.GetNextOrbAttackAtUtc(orb.ItemUid));
            first.Status = PlayerMatchStatus.ELIMINATED;
            var readyAt = first.Orbs.GetNextOrbAttackAtUtc(orb.ItemUid)!.Value;
            attacks.ActivateOrbs(match, first, readyAt);
            Assert.Empty(match.PendingWaveAttacks);
        }
    }
}
