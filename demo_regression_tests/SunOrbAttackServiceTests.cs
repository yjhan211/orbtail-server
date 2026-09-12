using game_server.matches;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SunOrbAttackServiceTests
{
    [Fact]
    public void EntryPointsRequireMatchLockAndEndedMatchDoesNotAdvanceBurns()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947604);
        var service = new SunOrbAttackService(
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), store.EventLogs);
        var attacks = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), new PlayerOrbTrailService(), store.EventLogs);
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 } };
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessSwarmCrossfires(match, now));
        Assert.Throws<InvalidOperationException>(() => service.ProcessSwarmSunBurns(match, now));
        Assert.Throws<InvalidOperationException>(() => service.CollectSwarmCrossfireAnchoredTargets(match));
        Assert.Throws<InvalidOperationException>(() => service.CollectSwarmCrossfireCappedOwners(match, now));
        Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(match, owner, default, now));
        using (match.Enter())
        {
            var victim = new Player { Profile = new PlayerInfo { PlayerId = 12 } };
            match.RegisterParticipant(victim);
            victim.SunBurn = new Player.SunBurnState(11, 107000010, AreaType.None, now.AddSeconds(5), now.AddSeconds(1));
            var before = victim.SunBurn;
            match.TryMarkEnded();
            service.ProcessSwarmSunBurns(match, now.AddSeconds(10));
            Assert.Equal(before, victim.SunBurn);
            Assert.False(attacks.TryStartSunCrossfire(match, owner, default, now));
        }
    }

    [Fact]
    public void Process_WaitsForTelegraphHitsOnceAndRemovesExpiredDodgeSnapshot()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947601);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new SunOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), logs);
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
            match.SunOrbAttacks.Shapes.Add(shape);
            match.SunOrbAttacks.DodgeSnapshot = SunOrbAttackService.BuildDodgeSnapshot(match.SunOrbAttacks.Shapes);
            owner.Player.CurrentArea = victim.Player.CurrentArea = AreaType.S2Ground;
            owner.Player.Position = victim.Player.Position = new Vector3f(2, Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y, 0);
            service.ProcessSwarmCrossfires(match, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.Empty(shape.HitVictims);

            var hitAt = now.AddSeconds(1.6);
            service.ProcessSwarmCrossfires(match, hitAt);
            int healthAfterHit = victim.Player.Health;
            Assert.InRange(healthAfterHit, 1, Config.MAX_HEALTH - 1);
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);
            Assert.Equal(12L, Assert.Single(shape.HitVictims));
            Assert.Equal(hitAt.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS), victim.Player.SunBurn!.Value.NextTickAtUtc);

            service.ProcessSwarmCrossfires(match, hitAt.AddSeconds(0.1));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            service.ProcessSwarmCrossfires(match, now.AddSeconds(3));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            Assert.Empty(match.SunOrbAttacks.Shapes);
            Assert.Empty(match.SunOrbAttacks.DodgeSnapshot);
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
        var service = new SunOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), logs);
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
            service.ProcessSwarmSunBurns(second, due);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            second.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(first))
        {
            var victim = burned;
            service.ProcessSwarmSunBurns(first, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            service.ProcessSwarmSunBurns(first, due);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) *
                Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            service.ProcessSwarmSunBurns(first, due);
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
        var shapes = match.SunOrbAttacks.Shapes;
        SwarmCrossfireShape first = CreateShape(eventId: 1, ownerId: 10, anchorCombatTargetId: 101, armedAtUtc: NowUtc.AddSeconds(1));
        SwarmCrossfireShape second = CreateShape(eventId: 2, ownerId: 10, anchorCombatTargetId: 102, armedAtUtc: NowUtc.AddSeconds(2));
        using var scope = MatchRuntimeStore.Enter(match);

        shapes.Add(first);
        shapes.Add(second);
        match.SunOrbAttacks.DodgeSnapshot = SunOrbAttackService.BuildDodgeSnapshot(shapes);

        Assert.Equal(2, SunOrbAttackService.CountTelegraphing(shapes, 10, NowUtc));
        Assert.Contains(10, service.CollectSwarmCrossfireCappedOwners(match, NowUtc));
        Assert.Equal(
            [(10L, 101L), (10L, 102L)],
            service.CollectSwarmCrossfireAnchoredTargets(match).OrderBy(anchor => anchor.CombatTargetId));

        Assert.Equal(1, SunOrbAttackService.CountTelegraphing(shapes, 10, NowUtc.AddSeconds(1)));
        Assert.DoesNotContain(10, service.CollectSwarmCrossfireCappedOwners(match, NowUtc.AddSeconds(1)));

        shapes.RemoveAt(1);
        match.SunOrbAttacks.DodgeSnapshot = SunOrbAttackService.BuildDodgeSnapshot(shapes);
        Assert.Single(shapes);
        Assert.Single(match.SunOrbAttacks.DodgeSnapshot);
        Assert.Equal([(10L, 101L)], service.CollectSwarmCrossfireAnchoredTargets(match));

        shapes.RemoveAt(0);
        match.SunOrbAttacks.DodgeSnapshot = SunOrbAttackService.BuildDodgeSnapshot(shapes);
        Assert.Empty(shapes);
        Assert.Empty(match.SunOrbAttacks.DodgeSnapshot);
        Assert.Empty(service.CollectSwarmCrossfireAnchoredTargets(match));
        Assert.Empty(service.CollectSwarmCrossfireCappedOwners(match, NowUtc));
    }

    [Fact]
    public void DodgeSnapshot_IsGroundScaledAndEachBuildIsAnIndependentArray()
    {
        var shapes = new List<SwarmCrossfireShape>();
        SwarmCrossfireShape first = CreateShape(
            eventId: 1, ownerId: 10, anchorCombatTargetId: 101, armedAtUtc: NowUtc.AddSeconds(1),
            origin: new Vector3f(1f, 2f, 0f), end: new Vector3f(4f, 4f, 0f), groundLength: 7f, halfWidth: 0.4f, sweepSpeed: 5f);

        shapes.Add(first);
        IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> firstSnapshot = SunOrbAttackService.BuildDodgeSnapshot(shapes);
        SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat threat = Assert.Single(firstSnapshot);

        Assert.Equal(first.Area, threat.Area);
        Assert.Equal(first.OwnerId, threat.OwnerId);
        Assert.Equal(1f, threat.OriginX);
        Assert.Equal(2f, threat.OriginY);
        Assert.Equal(0.6f, threat.AxisX, 3);
        Assert.Equal(0.8f, threat.AxisY, 3);
        Assert.Equal(7f, threat.GroundLength);
        Assert.Equal(0.4f, threat.HalfWidth);
        Assert.Equal(5f, threat.SweepSpeed);
        Assert.Equal(first.ArmedAtUtc, threat.ArmedAtUtc);
        Assert.Equal(first.ExpiresAtUtc, threat.ExpiresAtUtc);

        // 길이 0에 가까운 모양은 방향이 없어 스냅샷에서 빠진다.
        shapes.Add(CreateShape(eventId: 2, ownerId: 20, anchorCombatTargetId: 202, armedAtUtc: NowUtc,
            origin: new Vector3f(5f, 5f, 0f), end: new Vector3f(5.001f, 5.001f, 0f)));
        Assert.Single(SunOrbAttackService.BuildDodgeSnapshot(shapes));

        shapes.Add(CreateShape(eventId: 3, ownerId: 30, anchorCombatTargetId: 303, armedAtUtc: NowUtc,
            origin: new Vector3f(0f, 0f, 0f), end: new Vector3f(2f, 0f, 0f)));
        IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> expandedSnapshot = SunOrbAttackService.BuildDodgeSnapshot(shapes);
        Assert.Equal(2, expandedSnapshot.Count);
        Assert.Single(firstSnapshot);
        Assert.NotSame(firstSnapshot, expandedSnapshot);

        shapes.RemoveAt(2);
        Assert.Equal(2, expandedSnapshot.Count);
        Assert.Single(SunOrbAttackService.BuildDodgeSnapshot(shapes));
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

        service.ProcessSwarmSunBurns(match, NowUtc.AddSeconds(interval).AddMilliseconds(-1));
        Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);

        service.ProcessSwarmSunBurns(match, NowUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        Assert.Equal(NowUtc.AddSeconds(interval * 2), victim.Player.SunBurn!.Value.NextTickAtUtc);

        // 재피격은 지속·다음 틱을 새로 잡는다.
        DateTime refreshedAtUtc = NowUtc.AddSeconds(interval * 1.5);
        victim.Player.SunBurn = new Player.SunBurnState(11, 107000011, AreaType.S2Gym1, refreshedAtUtc.AddSeconds(interval * 3), refreshedAtUtc.AddSeconds(interval));
        service.ProcessSwarmSunBurns(match, NowUtc.AddSeconds(interval * 2));
        Assert.Equal(Config.MAX_HEALTH - tickDamage, victim.Player.Health);
        service.ProcessSwarmSunBurns(match, refreshedAtUtc.AddSeconds(interval));
        Assert.Equal(Config.MAX_HEALTH - tickDamage * 2, victim.Player.Health);

        // 만료 시각에 걸린 마지막 틱은 적용한 뒤 화상을 걷는다.
        service.ProcessSwarmSunBurns(match, refreshedAtUtc.AddSeconds(interval * 2));
        service.ProcessSwarmSunBurns(match, refreshedAtUtc.AddSeconds(interval * 3));
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

    private static SunOrbAttackService CreateService(MatchRuntimeStore store) =>
        new(TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), store.EventLogs);

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
}
