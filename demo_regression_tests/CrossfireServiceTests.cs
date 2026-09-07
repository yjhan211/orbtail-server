using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class CrossfireServiceTests
{
    [Fact]
    public void Process_WaitsForTelegraphHitsOnceAndRemovesExpiredDodgeSnapshot()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947601);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        var service = new CrossfireService(store, new MatchCombatDamageService(store, logs), logs);
        var now = DateTime.UtcNow;
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        using (store.Enter(match))
        {
            var shape = new SwarmCrossfireShape
            {
                EventId = 1, OwnerId = 11, WeaponItemId = 107000010, Damage = 10,
                Area = AreaType.S2Ground, Origin = new Vector3f(0, 0, 0),
                End = new Vector3f(6, 0, 0), GroundLength = 6, HalfWidth = 0.35f,
                SweepSpeed = 5, ArmedAtUtc = now.AddSeconds(1), ExpiresAtUtc = now.AddSeconds(3),
                LastFront = -0.35f, DetonateAtWall = false
            };
            match.Swarm.Crossfire.AddShape(shape);
            List<SwarmParticipantSpatial> participants =
            [
                new(11, AreaType.S2Ground, new Vector3f(2, Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y, 0)),
                new(12, AreaType.S2Ground, new Vector3f(2, Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y, 0))
            ];
            service.ProcessSwarmCrossfires(match.MatchingId, now, participants, [], [owner, victim], []);
            Assert.Equal(0, victim.Corruption);
            Assert.Empty(shape.HitVictims);

            var hitAt = now.AddSeconds(1.6);
            service.ProcessSwarmCrossfires(match.MatchingId, hitAt, participants, [], [owner, victim], []);
            int firstDamage = victim.Corruption;
            Assert.True(firstDamage > 0);
            Assert.Equal(0, owner.Corruption);
            Assert.Equal(12L, Assert.Single(shape.HitVictims));
            Assert.True(match.Swarm.Crossfire.TryGetSunBurn(12, out var burn));
            Assert.Equal(hitAt.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS), burn!.NextTickAtUtc);

            service.ProcessSwarmCrossfires(match.MatchingId, hitAt.AddSeconds(0.1),
                participants, [], [owner, victim], []);
            Assert.Equal(firstDamage, victim.Corruption);
            service.ProcessSwarmCrossfires(match.MatchingId, now.AddSeconds(3),
                participants, [], [owner, victim], []);
            Assert.Equal(firstDamage, victim.Corruption);
            Assert.Equal(0, match.Swarm.Crossfire.ShapeCount);
            Assert.Empty(match.Swarm.Crossfire.DodgeSnapshot);
            match.TryMarkTerminal();
        }
    }

    [Fact]
    public void BurnTick_AppliesOnlyToOwningMatchAndDoesNotRepeatAtSameTime()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947602);
        var second = store.GetOrCreate(947603);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        var service = new CrossfireService(store, new MatchCombatDamageService(store, logs), logs);
        var now = DateTime.UtcNow;
        using (store.Enter(first))
            first.Swarm.Crossfire.SetSunBurn(12, 11, 107000010, AreaType.S2Ground,
                now, Config.SWARM_SUN_BURN_SECONDS, Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
        var due = now.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
        using (store.Enter(second))
        {
            var victim = new BotPlayerState { PlayerId = 12 };
            service.ProcessSwarmSunBurns(second.MatchingId, due, [], [victim], []);
            Assert.Equal(0, victim.Corruption);
            second.TryMarkTerminal();
        }
        using (store.Enter(first))
        {
            var victim = new BotPlayerState { PlayerId = 12 };
            service.ProcessSwarmSunBurns(first.MatchingId, now, [], [victim], []);
            Assert.Equal(0, victim.Corruption);
            service.ProcessSwarmSunBurns(first.MatchingId, due, [], [victim], []);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_CORRUPTION) *
                Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER));
            Assert.Equal(expected, victim.Corruption);
            service.ProcessSwarmSunBurns(first.MatchingId, due, [], [victim], []);
            Assert.Equal(expected, victim.Corruption);
            first.TryMarkTerminal();
        }
    }
}
