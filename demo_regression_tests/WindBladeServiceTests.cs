using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class WindBladeServiceTests
{
    public WindBladeServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void Process_WaitsForSpinupThenShocksBeforeWoundingAndHonorsImmunity()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947501);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var trails = new OrbTrailService(store);
        var service = new WindBladeService(store, trails, new MatchCombatDamageService(store, logs, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchCombatDamageService>.Instance), logs);
        var now = DateTime.UtcNow;
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        using (MatchRuntimeStore.Enter(match))
        {
            match.Inventory.GetPlayerInventory(11).AddItem(107000020, forceSeparateStack: true);
            var origin = trails.GetSwarmOrbTrailPosition(match.MatchingId, 11, 0, new Vector3f(0, 0, 0));
            List<SwarmParticipantSpatial> participants =
                [new(11, AreaType.None, new Vector3f(0, 0, 0)), new(12, AreaType.None, origin)];
            service.Process(match.MatchingId, now, participants, [], [owner, victim], []);
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            Assert.False(match.Swarm.WindBlade.IsWounded(12, now));

            var hitAt = now.AddSeconds(Math.Max(Config.SWARM_WIND_BLADE_TICK_SECONDS,
                Config.SWARM_WIND_BLADE_SPINUP_SECONDS) + 0.001);
            service.Process(match.MatchingId, hitAt, participants, [], [owner, victim], []);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE)));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Health);
            Assert.True(match.Swarm.WindBlade.IsWounded(12, hitAt));
            Assert.Equal(Config.MAX_HEALTH, owner.Health);

            service.Process(match.MatchingId, hitAt.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS + 0.001),
                participants, [], [owner, victim], []);
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Health);
            match.TryMarkTerminal();
        }
    }

    [Theory]
    [InlineData(107000010, false)]
    [InlineData(107000020, true)]
    public void Process_DoesNotHitWithOtherColorOrAcrossAreas(int itemId, bool otherArea)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947502);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var trails = new OrbTrailService(store);
        var service = new WindBladeService(store, trails, new MatchCombatDamageService(store, logs, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchCombatDamageService>.Instance), logs);
        using (MatchRuntimeStore.Enter(match))
        {
            match.Inventory.GetPlayerInventory(11).AddItem(itemId, forceSeparateStack: true);
            var victim = new BotPlayerState { PlayerId = 12 };
            var origin = trails.GetSwarmOrbTrailPosition(match.MatchingId, 11, 0, new Vector3f(0, 0, 0));
            List<SwarmParticipantSpatial> participants =
                [new(11, AreaType.None, new Vector3f(0, 0, 0)), new(12, otherArea ? (AreaType)1 : AreaType.None, origin)];
            var now = DateTime.UtcNow;
            service.Process(match.MatchingId, now, participants, [], [victim], []);
            service.Process(match.MatchingId, now.AddSeconds(1), participants, [], [victim], []);
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            Assert.False(match.Swarm.WindBlade.IsWounded(12, now.AddSeconds(1)));
            match.TryMarkTerminal();
        }
    }

    [Theory]
    [InlineData(1f, 0f, true)]
    [InlineData(0f, 0.5f, true)]
    [InlineData(0f, 0.51f, false)]
    public void GroundRadius_UsesScaledYAndIncludesBoundary(float x, float y, bool expected)
    {
        Assert.Equal(expected, SwarmCombatGeometry.IsWithinGroundRadius(
            new Vector3f(0, 0, 0), new Vector3f(x, y, 0), 1));
    }
}
