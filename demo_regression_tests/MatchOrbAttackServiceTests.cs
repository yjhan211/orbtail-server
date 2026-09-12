using game_server.matches;
using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOrbAttackServiceTests
{
    public MatchOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void EntryPointsRequireMatchLockAndEndedMatchDoesNotAdvanceBurns()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947604);
        var service = new MatchOrbAttackService(
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), store.EventLogs, new MatchMonsterService(new MonsterMovementService()));
        var attacks = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), new PlayerOrbTrailService(), store.EventLogs, new MatchMonsterService(new MonsterMovementService()));
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 } };
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessSunCrossfires(match, now));
        Assert.Throws<InvalidOperationException>(() => service.ProcessSunBurns(match, now));
        Assert.Throws<InvalidOperationException>(() => service.CollectSunCrossfireAnchoredTargets(match));
        Assert.Throws<InvalidOperationException>(() => service.CollectSunCrossfireCappedOwners(match, now));
        Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(match, owner, default, now));
        using (match.Enter())
        {
            var victim = new Player { Profile = new PlayerInfo { PlayerId = 12 } };
            match.RegisterParticipant(victim);
            victim.SunBurn = new Player.SunBurnState(11, 107000010, AreaType.None, now.AddSeconds(5), now.AddSeconds(1));
            var before = victim.SunBurn;
            match.TryMarkEnded();
            service.ProcessSunBurns(match, now.AddSeconds(10));
            Assert.Equal(before, victim.SunBurn);
            Assert.False(attacks.TryStartSunCrossfire(match, owner, default, now));
        }
    }

    [Fact]
    public void Process_WaitsForTelegraphHitsOnceAndRemovesExpiredShape()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947601);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new MatchOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), logs, new MatchMonsterService(new MonsterMovementService()));
        var now = DateTime.UtcNow;
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        match.RegisterParticipant(owner.Player);
        match.RegisterParticipant(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            var shape = new SwarmCrossfireShape
            {
                EventId = 1, OwnerId = 11, WeaponItemId = 107000010, Damage = 10,
                Area = AreaType.S2Ground, Origin = new Vector3f(0, 0, 0),
                End = new Vector3f(6, 0, 0), GroundLength = 6, HalfWidth = 0.35f,
                SweepSpeed = 5, ArmedAtUtc = now.AddSeconds(1), ExpiresAtUtc = now.AddSeconds(3),
                LastFront = -0.35f, DetonateAtWall = false
            };
            match.SunCrossfireShapes.Add(shape);
            owner.Player.CurrentArea = victim.Player.CurrentArea = AreaType.S2Ground;
            owner.Player.Position = victim.Player.Position = new Vector3f(2, Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y, 0);
            service.ProcessSunCrossfires(match, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.Empty(shape.HitVictims);

            var hitAt = now.AddSeconds(1.6);
            service.ProcessSunCrossfires(match, hitAt);
            int healthAfterHit = victim.Player.Health;
            Assert.InRange(healthAfterHit, 1, Config.MAX_HEALTH - 1);
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);
            Assert.Equal(12L, Assert.Single(shape.HitVictims));
            Assert.Equal(hitAt.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS), victim.Player.SunBurn!.Value.NextTickAtUtc);

            service.ProcessSunCrossfires(match, hitAt.AddSeconds(0.1));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            service.ProcessSunCrossfires(match, now.AddSeconds(3));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            Assert.Empty(match.SunCrossfireShapes);
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void BurnTick_AppliesOnlyToOwningMatchAndDoesNotRepeatAtSameTime()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947602);
        var second = store.GetOrCreate(947603);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new MatchOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), logs, new MatchMonsterService(new MonsterMovementService()));
        var now = DateTime.UtcNow;
        var burned = new BotPlayerState { PlayerId = 12 };
        using (MatchRuntimeStore.Enter(first))
        {
            first.RegisterParticipant(burned.Player);
            burned.Player.SunBurn = new Player.SunBurnState(11, 107000010, AreaType.S2Ground,
                now.AddSeconds(Config.SWARM_SUN_BURN_SECONDS), now.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS));
        }
        var due = now.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
        using (MatchRuntimeStore.Enter(second))
        {
            var victim = new BotPlayerState { PlayerId = 12 };
            second.RegisterParticipant(victim.Player);
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
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) *
                Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            service.ProcessSunBurns(first, due);
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            first.TryMarkEnded();
        }
    }

    private static readonly DateTime NowUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShapeQueries_PreserveTelegraphBoundariesAnchorsAndRemovalLifecycle()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(43000);
        var service = CreateService(store);
        var shapes = match.SunCrossfireShapes;
        SwarmCrossfireShape first = CreateShape(eventId: 1, ownerId: 10, anchorCombatTargetId: 101, armedAtUtc: NowUtc.AddSeconds(1));
        SwarmCrossfireShape second = CreateShape(eventId: 2, ownerId: 10, anchorCombatTargetId: 102, armedAtUtc: NowUtc.AddSeconds(2));
        using var scope = MatchRuntimeStore.Enter(match);

        shapes.Add(first);
        shapes.Add(second);

        Assert.Equal(2, MatchOrbAttackService.CountTelegraphing(shapes, 10, NowUtc));
        Assert.Contains(10, service.CollectSunCrossfireCappedOwners(match, NowUtc));
        Assert.Equal(
            [(10L, 101L), (10L, 102L)],
            service.CollectSunCrossfireAnchoredTargets(match).OrderBy(anchor => anchor.CombatTargetId));

        Assert.Equal(1, MatchOrbAttackService.CountTelegraphing(shapes, 10, NowUtc.AddSeconds(1)));
        Assert.DoesNotContain(10, service.CollectSunCrossfireCappedOwners(match, NowUtc.AddSeconds(1)));

        shapes.RemoveAt(1);
        Assert.Single(shapes);
        Assert.Equal([(10L, 101L)], service.CollectSunCrossfireAnchoredTargets(match));

        shapes.RemoveAt(0);
        Assert.Empty(shapes);
        Assert.Empty(service.CollectSunCrossfireAnchoredTargets(match));
        Assert.Empty(service.CollectSunCrossfireCappedOwners(match, NowUtc));
    }

    [Fact]
    public void SunBurn_TicksOnInclusiveDueBoundaryRefreshesPayloadAndExpiresAfterLastTick()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(43002);
        var service = CreateService(store);
        var victim = new BotPlayerState { PlayerId = 20 };
        using var scope = MatchRuntimeStore.Enter(match);
        match.RegisterParticipant(victim.Player);
        int tickDamage = Math.Max(1, (int)MathF.Round(
            Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) * Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
        double interval = Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS;

        victim.Player.SunBurn = new Player.SunBurnState(10, 107000010, AreaType.S2Ground, NowUtc.AddSeconds(interval * 3), NowUtc.AddSeconds(interval));

        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval).AddMilliseconds(-1));
        Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);

        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        Assert.Equal(NowUtc.AddSeconds(interval * 2), victim.Player.SunBurn!.Value.NextTickAtUtc);

        // 재피격은 지속·다음 틱을 새로 잡는다.
        DateTime refreshedAtUtc = NowUtc.AddSeconds(interval * 1.5);
        victim.Player.SunBurn = new Player.SunBurnState(11, 107000011, AreaType.S2Gym1, refreshedAtUtc.AddSeconds(interval * 3), refreshedAtUtc.AddSeconds(interval));
        service.ProcessSunBurns(match, NowUtc.AddSeconds(interval * 2));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage * 2, victim.Player.Health);

        // 만료 시각에 걸린 마지막 틱은 적용한 뒤 화상을 걷는다.
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval * 2));
        service.ProcessSunBurns(match, refreshedAtUtc.AddSeconds(interval * 3));
        Assert.Equal(Config.MAX_HEALTH - tickDamage * 4, victim.Player.Health);
        Assert.Null(victim.Player.SunBurn);
    }

    [Fact]
    public void Convergence_UsesInclusiveOneSecondWindowAndResetsAfterIt()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(43003);
        var logs = store.EventLogs;

        SwarmCrossfireConvergenceObservation first = logs.LogCrossfireHit(match.MatchingId, 100, NowUtc);
        SwarmCrossfireConvergenceObservation exactBoundary = logs.LogCrossfireHit(match.MatchingId, 100, NowUtc.AddSeconds(1));
        SwarmCrossfireConvergenceObservation afterBoundary = logs.LogCrossfireHit(match.MatchingId, 100, NowUtc.AddSeconds(1).AddTicks(1));
        SwarmCrossfireConvergenceObservation otherTarget = logs.LogCrossfireHit(match.MatchingId, 200, NowUtc.AddSeconds(1).AddTicks(1));

        Assert.Equal(1, first.HitCount);
        Assert.Equal(0d, first.WindowMilliseconds);
        Assert.Equal(2, exactBoundary.HitCount);
        Assert.Equal(1000d, exactBoundary.WindowMilliseconds);
        Assert.Equal(1, afterBoundary.HitCount);
        Assert.Equal(0d, afterBoundary.WindowMilliseconds);
        Assert.Equal(1, otherTarget.HitCount);
    }

    private static MatchOrbAttackService CreateService(MatchRuntimeStore store) =>
        new(TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), store.EventLogs, new MatchMonsterService(new MonsterMovementService()));

    private static SwarmCrossfireShape CreateShape(
        long eventId,
        long ownerId,
        long anchorCombatTargetId,
        DateTime armedAtUtc,
        Vector3f? origin = null,
        Vector3f? end = null,
        float groundLength = 6f,
        float halfWidth = 0.35f,
        float sweepSpeed = 4.5f) =>
        new()
        {
            EventId = eventId,
            OwnerId = ownerId,
            WeaponItemId = 101,
            Damage = 10,
            Area = AreaType.S2Ground,
            Origin = origin ?? new Vector3f(0f, 0f, 0f),
            End = end ?? new Vector3f(6f, 0f, 0f),
            GroundLength = groundLength,
            HalfWidth = halfWidth,
            BlastRadius = 0.5f,
            SweepSpeed = sweepSpeed,
            ArmedAtUtc = armedAtUtc,
            ExpiresAtUtc = armedAtUtc.AddSeconds(2),
            AnchorMonsterId = 301,
            AnchorCombatTargetId = anchorCombatTargetId,
            LastFront = -halfWidth
        };

    [Fact]
    public void WaveWaitsForTargetAndFuseThenDetonatesOnce()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<MatchOrbAttackService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948501);
        var trails = provider.GetRequiredService<PlayerOrbTrailService>();
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f(), CurrentArea = AreaType.S2Ground };
        var victim = new Player { Profile = new PlayerInfo { PlayerId = -11 }, Position = new Vector3f(100, 100, 0), CurrentArea = AreaType.S2Ground };
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(owner);
            match.RegisterParticipant(victim);
            var orb = owner.Orbs.AddItem(107000030);
            attacks.ActivateWaveOrbs(match, owner, now);
            Assert.Empty(match.PendingWaveAttacks);
            long key = orb.ItemUid;
            var readyAt = owner.WaveOrbNextAttackAt(key)!.Value;
            Assert.True(readyAt > now);
            attacks.ActivateWaveOrbs(match, owner, readyAt);
            Assert.Empty(match.PendingWaveAttacks);
            Assert.Equal(readyAt, owner.WaveOrbNextAttackAt(key)!.Value);
            victim.Position = trails.GetOrbPosition(match, owner, 0, owner.Position);
            attacks.ActivateWaveOrbs(match, owner, readyAt);
            var pending = Assert.Single(match.PendingWaveAttacks);
            Assert.True(pending.ExplodeAtUtc > readyAt);
            owner.Orbs.TakeAllItems();
            owner.Status = PlayerMatchStatus.ELIMINATED;
            service.ProcessWaveDetonations(match, pending.ExplodeAtUtc.AddTicks(-1));
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            service.ProcessWaveDetonations(match, pending.ExplodeAtUtc);
            Assert.Empty(match.PendingWaveAttacks);
            Assert.True(victim.Health < Config.MAX_HEALTH);
            int health = victim.Health;
            service.ProcessWaveDetonations(match, pending.ExplodeAtUtc);
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
        Assert.Throws<InvalidOperationException>(() => service.ProcessWaveDetonations(first, now));
        using (first.Enter())
        {
            first.PendingWaveAttacks.Add(new PendingWaveAttack(1, AreaType.None, new Vector3f(), 1, 1, 107000030, now));
            first.TryMarkEnded();
            service.ProcessWaveDetonations(first, now);
            Assert.Single(first.PendingWaveAttacks);
        }
        using (second.Enter())
        {
            service.ProcessWaveDetonations(second, now);
            Assert.Empty(second.PendingWaveAttacks);
        }
    }

    [Fact]
    public void PlayerActivationRequiresLockAndOwnMatchAndKeepsClocksOnPlayer()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var match = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948504);
        var first = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f() };
        var second = new Player { Profile = new PlayerInfo { PlayerId = 12 }, Position = new Vector3f() };
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => attacks.ActivateWaveOrbs(match, first, now));
        using (match.Enter())
        {
            Assert.Throws<InvalidOperationException>(() => attacks.ActivateWaveOrbs(match, first, now));
            match.RegisterParticipant(first);
            match.RegisterParticipant(second);
            var orb = first.Orbs.AddItem(107000030);
            attacks.ActivateWaveOrbs(match, first, now);
            Assert.NotNull(first.WaveOrbNextAttackAt(orb.ItemUid));
            Assert.Null(second.WaveOrbNextAttackAt(orb.ItemUid));
            first.Status = PlayerMatchStatus.ELIMINATED;
            var readyAt = first.WaveOrbNextAttackAt(orb.ItemUid)!.Value;
            attacks.ActivateWaveOrbs(match, first, readyAt);
            Assert.Empty(match.PendingWaveAttacks);
        }
    }
}
