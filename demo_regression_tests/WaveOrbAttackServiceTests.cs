using game_server.matches;
using game_server.matches.combat;
using game_server.players;
using Microsoft.Extensions.DependencyInjection;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class WaveOrbAttackServiceTests
{
    public WaveOrbAttackServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void WaveWaitsForTargetAndFuseThenDetonatesOnce()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<WaveOrbAttackService>();
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
            service.ProcessTick(match, pending.ExplodeAtUtc.AddTicks(-1));
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            service.ProcessTick(match, pending.ExplodeAtUtc);
            Assert.Empty(match.PendingWaveAttacks);
            Assert.True(victim.Health < Config.MAX_HEALTH);
            int health = victim.Health;
            service.ProcessTick(match, pending.ExplodeAtUtc);
            Assert.Equal(health, victim.Health);
        }
    }

    [Fact]
    public void StateIsMatchOwnedAndTickRequiresLockAndSkipsEndedMatch()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var service = provider.GetRequiredService<WaveOrbAttackService>();
        var store = provider.GetRequiredService<MatchRuntimeStore>();
        var first = store.GetOrCreate(948502);
        var second = store.GetOrCreate(948503);
        var now = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() => service.ProcessTick(first, now));
        Assert.Throws<InvalidOperationException>(() => service.Detonate(first, 1, AreaType.None, new Vector3f(), 1, 1, 107000030, now));
        using (first.Enter())
        {
            first.PendingWaveAttacks.Add(new PendingWaveAttack(1, AreaType.None, new Vector3f(), 1, 1, 107000030, now));
            first.TryMarkEnded();
            service.ProcessTick(first, now);
            Assert.Single(first.PendingWaveAttacks);
        }
        using (second.Enter())
        {
            service.ProcessTick(second, now);
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
