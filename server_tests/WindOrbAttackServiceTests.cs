using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace server_tests;

public sealed class WindOrbAttackServiceTests
{
    public WindOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void ProcessRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947503);
        var owner = new Bot { PlayerId = 11 };
        var service = new PlayerOrbService();
        var attacks = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store)), service, new MatchSynchronizationService());
        Assert.Throws<InvalidOperationException>(() => attacks.ProcessTick(match, DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => attacks.ProcessWindAttacks(match, DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => service.ActivateOrbs(match, owner.Player, DateTime.UtcNow));
        using (match.Enter())
        {
            match.TryMarkEnded();
            service.ActivateOrbs(match, owner.Player, DateTime.UtcNow);
        }
    }

    [Fact]
    public void Process_HitsAtFirstPhaseAndHonorsImmunity()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947501);
        var service = new PlayerOrbService();
        var attacks = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store, NullLogger.Instance)), service, new MatchSynchronizationService());
        var now = DateTime.UtcNow;
        var owner = new Bot { PlayerId = 11 };
        var victim = new Bot { PlayerId = 12 };
        match.RegisterPlayer(owner.Player);
        match.RegisterPlayer(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            var orb = TestGameSessionServices.Orbs(match, 11).AddOrb(107000020);
            var origin = PlayerOrbTrailService.GetOrbPosition(match, owner.Player, 0, new Vector3f(0, 0, 0), PlayerOrbTrailService.GetOrbTiersInOrder(match, owner.Player));
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            service.ActivateOrbs(match, owner.Player, now);
            attacks.ProcessWindAttacks(match, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            var hitAt = TestGameSessionServices.Orbs(match, 11).GetNextOrbAttackAtUtc(orb.ItemUid)!.Value;
            service.ActivateOrbs(match, owner.Player, hitAt);
            Assert.Single(match.PendingWindAttacks);
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            attacks.ProcessWindAttacks(match, hitAt);
            Assert.Empty(match.PendingWindAttacks);
            int expected = Math.Max(1, (int)MathF.Round(
                Config.ScaleSwarmDamageTaken(Config.SWARM_ORB_SHOCK_DAMAGE)));
            Assert.Equal(Config.MAX_HEALTH - expected, victim.Player.Health);
            Assert.True(victim.Player.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, hitAt));
            Assert.Equal(Config.MAX_HEALTH, owner.Player.Health);

            var immuneAt = hitAt.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS + 0.001);
            service.ActivateOrbs(match, owner.Player, immuneAt);
            attacks.ProcessWindAttacks(match, immuneAt);
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
        var service = new PlayerOrbService();
        var attacks = new MatchOrbAttackService(TestGameSessionServices.CreateCombatDamageService(TestGameSessionServices.CreateHealthService(store, NullLogger.Instance)), service, new MatchSynchronizationService());
        var owner = new Bot { PlayerId = 11 };
        var victim = new Bot { PlayerId = 12 };
        match.RegisterPlayer(owner.Player);
        match.RegisterPlayer(victim.Player);
        using (MatchRuntimeStore.Enter(match))
        {
            TestGameSessionServices.Orbs(match, 11).AddOrb(itemId);
            var origin = PlayerOrbTrailService.GetOrbPosition(match, owner.Player, 0, new Vector3f(0, 0, 0), PlayerOrbTrailService.GetOrbTiersInOrder(match, owner.Player));
            owner.Player.Position = new Vector3f(0, 0, 0);
            victim.Player.Position = origin;
            if (otherArea) victim.Player.Position = TestMapPosition.In(AreaType.S2Library1);
            var now = DateTime.UtcNow;
            service.ActivateOrbs(match, owner.Player, now);
            attacks.ProcessWindAttacks(match, now);
            service.ActivateOrbs(match, owner.Player, now.AddSeconds(1));
            attacks.ProcessWindAttacks(match, now.AddSeconds(1));
            Assert.Equal(Config.MAX_HEALTH, victim.Player.Health);
            Assert.False(victim.Player.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, now.AddSeconds(1)));
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
