using game_server.matches;
using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class WindOrbAttackServiceTests
{
    public WindOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void ProcessRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947503);
        var owner = new BotPlayerState { PlayerId = 11 };
        var service = new PlayerOrbService(
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), TestGameSessionServices.CreateCombatDamageService(store.EventLogs), new PlayerOrbTrailService(), store.EventLogs, new MatchMonsterService(new MonsterMovementService()));
        Assert.Throws<InvalidOperationException>(() => service.ActivateWindOrbs(match, owner.Player, DateTime.UtcNow));
        using (match.Enter())
        {
            match.TryMarkEnded();
            service.ActivateWindOrbs(match, owner.Player, DateTime.UtcNow);
        }
    }

    [Fact]
    public void Process_WaitsForSpinupThenShocksBeforeWoundingAndHonorsImmunity()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947501);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var trails = new PlayerOrbTrailService();
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), trails, logs, new MatchMonsterService(new MonsterMovementService()));
        var now = DateTime.UtcNow;
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        match.RegisterParticipant(owner.Player);
        match.RegisterParticipant(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            TestGameSessionServices.Orbs(match, 11).AddItem(107000020);
            var origin = trails.GetOrbPosition(match, owner.Player, 0, new Vector3f(0, 0, 0));
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            service.ActivateWindOrbs(match, owner.Player, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.False(victim.Player.IsWounded(now));

            var hitAt = now.AddSeconds(Math.Max(Config.SWARM_WIND_BLADE_TICK_SECONDS,
                Config.SWARM_WIND_BLADE_SPINUP_SECONDS) + 0.001);
            service.ActivateWindOrbs(match, owner.Player, hitAt);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE)));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            Assert.True(victim.Player.IsWounded(hitAt));
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);

            service.ActivateWindOrbs(match, owner.Player, hitAt.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS + 0.001));
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
        var trails = new PlayerOrbTrailService();
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store, logs, new game_server.matches.MatchSummaryFileStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), TestGameSessionServices.CreateCombatDamageService(logs), trails, logs, new MatchMonsterService(new MonsterMovementService()));
        var owner = new BotPlayerState { PlayerId = 11 };
        var victim = new BotPlayerState { PlayerId = 12 };
        match.RegisterParticipant(owner.Player);
        match.RegisterParticipant(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            TestGameSessionServices.Orbs(match, 11).AddItem(itemId);
            var origin = trails.GetOrbPosition(match, owner.Player, 0, new Vector3f(0, 0, 0));
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            victim.Player.CurrentArea = otherArea ? (AreaType)1 : AreaType.None;
            var now = DateTime.UtcNow;
            service.ActivateWindOrbs(match, owner.Player, now);
            service.ActivateWindOrbs(match, owner.Player, now.AddSeconds(1));
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.False(victim.Player.IsWounded(now.AddSeconds(1)));
            match.TryMarkEnded();
        }
    }

    [Theory]
    [InlineData(1f, 0f, true)]
    [InlineData(0f, 0.5f, true)]
    [InlineData(0f, 0.51f, false)]
    public void GroundRadius_UsesScaledYAndIncludesBoundary(float x, float y, bool expected)
    {
        Assert.Equal(expected, GroundGeometry.IsWithinGroundRadius(
            new Vector3f(0, 0, 0), new Vector3f(x, y, 0), 1));
    }
}
