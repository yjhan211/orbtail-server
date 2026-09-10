using game_server.players.bots;
using game_server.logging;
using game_server.orbs;
using game_server.combat;
using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class WindOrbAttackServiceTests
{
    public WindOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void Process_WaitsForSpinupThenShocksBeforeWoundingAndHonorsImmunity()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947501);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var trails = new OrbTrailService(store);
        var service = new WindOrbAttackService(store, TestGameSessionServices.CreateEliminationService(store, logs, new game_server.matches.results.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), trails, logs);
        var now = DateTime.UtcNow;
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        match.RegisterParticipant(owner.Player);
        match.RegisterParticipant(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            match.Inventory.GetPlayerInventory(11).AddItem(107000020, forceSeparateStack: true);
            var origin = trails.GetSwarmOrbTrailPosition(match.MatchingId, 11, 0, new Vector3f(0, 0, 0));
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            service.Process(match.MatchingId, now, [owner.Player, victim.Player], []);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.False(match.WindOrbAttacks.IsWounded(12, now));

            var hitAt = now.AddSeconds(Math.Max(Config.SWARM_WIND_BLADE_TICK_SECONDS,
                Config.SWARM_WIND_BLADE_SPINUP_SECONDS) + 0.001);
            service.Process(match.MatchingId, hitAt, [owner.Player, victim.Player], []);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE)));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            Assert.True(match.WindOrbAttacks.IsWounded(12, hitAt));
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);

            service.Process(match.MatchingId, hitAt.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS + 0.001),
                [owner.Player, victim.Player], []);
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            match.TryMarkEnded();
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
        var service = new WindOrbAttackService(store, TestGameSessionServices.CreateEliminationService(store, logs, new game_server.matches.results.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), trails, logs);
        using (MatchRuntimeStore.Enter(match))
        {
            match.Inventory.GetPlayerInventory(11).AddItem(itemId, forceSeparateStack: true);
            var victim = new BotPlayerState { PlayerId = 12 };
            match.RegisterParticipant(victim.Player);
            var origin = trails.GetSwarmOrbTrailPosition(match.MatchingId, 11, 0, new Vector3f(0, 0, 0));
            var owner = new BotPlayerState { PlayerId = 11 };
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            victim.Player.CurrentArea = otherArea ? (AreaType)1 : AreaType.None;
            var now = DateTime.UtcNow;
            service.Process(match.MatchingId, now, [owner.Player, victim.Player], []);
            service.Process(match.MatchingId, now.AddSeconds(1), [owner.Player, victim.Player], []);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.False(match.WindOrbAttacks.IsWounded(12, now.AddSeconds(1)));
            match.TryMarkEnded();
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
