using game_server.players;
using game_server.combat;
using game_server.logging;
using game_server.matches;
using game_server.orbs;
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
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), store.EventLogs);
        var attacks = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store, store.EventLogs), new PlayerOrbTrailService(), store.EventLogs);
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 } };
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessSwarmCrossfires(match, now));
        Assert.Throws<InvalidOperationException>(() => service.ProcessSwarmSunBurns(match, now));
        Assert.Throws<InvalidOperationException>(() => service.CollectSwarmCrossfireAnchoredTargets(match));
        Assert.Throws<InvalidOperationException>(() => service.CollectSwarmCrossfireCappedOwners(match, now));
        Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(match, owner, default, now));
        using (match.Enter())
        {
            match.SunOrbAttacks.SetSunBurn(12, 11, 107000010, AreaType.None, now, 5, 1);
            Assert.True(match.SunOrbAttacks.TryGetSunBurn(12, out var before));
            match.TryMarkEnded();
            service.ProcessSwarmSunBurns(match, now.AddSeconds(10));
            Assert.True(match.SunOrbAttacks.TryGetSunBurn(12, out var after));
            Assert.Equal(before, after);
            Assert.False(attacks.TryStartSunCrossfire(match, owner, default, now));
        }
    }

    [Fact]
    public void Process_WaitsForTelegraphHitsOnceAndRemovesExpiredDodgeSnapshot()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947601);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new SunOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.results.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), logs);
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
            match.SunOrbAttacks.AddShape(shape);
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
            Assert.True(match.SunOrbAttacks.TryGetSunBurn(12, out var burn));
            Assert.Equal(hitAt.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS), burn!.NextTickAtUtc);

            service.ProcessSwarmCrossfires(match, hitAt.AddSeconds(0.1));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            service.ProcessSwarmCrossfires(match, now.AddSeconds(3));
            Assert.Equal(healthAfterHit, victim.Player.Health);
            Assert.Equal(0, match.SunOrbAttacks.ShapeCount);
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
        var service = new SunOrbAttackService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.results.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), logs);
        var now = DateTime.UtcNow;
        using (MatchRuntimeStore.Enter(first))
            first.SunOrbAttacks.SetSunBurn(12, 11, 107000010, AreaType.S2Ground,
                now, Config.SWARM_SUN_BURN_SECONDS, Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
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
            var victim = new BotPlayerState { PlayerId = 12 };
            first.RegisterParticipant(victim.Player);
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
}
