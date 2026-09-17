using game_server.matches;
using game_server.players;
using Microsoft.Extensions.DependencyInjection;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbServiceTests
{
    public PlayerOrbServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedOrbsKeepIndependentTimersAndOriginalOrdinals(bool windFirst)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948603);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var trails = provider.GetRequiredService<PlayerOrbTrailService>();
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var victim = new Player(new PlayerInfo { PlayerId = 12 }) { Position = new Vector3f() };
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            runtime.RegisterPlayer(victim);
            owner.Orbs.AddOrb(107000010);
            owner.Orbs.AddOrb(windFirst ? 107000020 : 107000030);
            owner.Orbs.AddOrb(windFirst ? 107000030 : 107000020);
            var orbs = owner.Orbs.GetOrderedOrbs();
            int windOrdinal = windFirst ? 1 : 2;
            int waveOrdinal = windFirst ? 2 : 1;
            var tiers = PlayerOrbTrailService.GetOrbTiersInOrder(runtime, owner);
            victim.Position = trails.GetOrbPosition(runtime, owner, windOrdinal, owner.Position, tiers);

            // 바람은 즉시 발동하고 파도는 자신의 첫 지연을 유지한다.
            attacks.ActivateOrbs(runtime, owner, now);
            Assert.True(victim.Health < Config.MAX_HEALTH);
            Assert.Empty(runtime.PendingWaveAttacks);
            Assert.False(owner.Orbs.IsOrbAttackReady(orbs[windOrdinal].ItemUid, now));
            Assert.True(owner.Orbs.IsOrbAttackReady(orbs[windOrdinal].ItemUid,
                now.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS)));
            Assert.True(owner.Orbs.IsOrbAttackReady(orbs[0].ItemUid, now));
            var readyAt = owner.Orbs.GetNextWaveOrbAttackAtUtc(orbs[waveOrdinal].ItemUid)!.Value;
            Assert.True(readyAt > now);

            // 색상별로 순서를 다시 매기지 않고 전체 보유 순서의 위치에서 발동한다.
            var wavePosition = trails.GetOrbPosition(runtime, owner, waveOrdinal, owner.Position, tiers);
            victim.Position = wavePosition;
            attacks.ActivateOrbs(runtime, owner, readyAt);
            Assert.Single(runtime.PendingWaveAttacks);
            Assert.True(owner.Orbs.GetNextWaveOrbAttackAtUtc(orbs[waveOrdinal].ItemUid) > readyAt);
            attacks.ActivateOrbs(runtime, owner, readyAt);
            Assert.Single(runtime.PendingWaveAttacks);
        }
    }

    [Fact]
    public void SunActivationCapsTelegraphsAndProjectilesOutliveOwner()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948601);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var projectiles = provider.GetRequiredService<MatchOrbAttackService>();
        var cell = MatchSpawnData.GetCorridorAnchor(1);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = position };
        var anchor = new Vector3f(position.X + 2, position.Y + 1, 0);
        var attack = new ProximityCombatAttack(11, -100, area, 107000010, 10,
            Origin: position, AnchorPosition: anchor);
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            for (int i = 0; i < Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER; i++)
                Assert.True(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            Assert.False(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            var shape = runtime.SunCrossfireShapes[0];
            Assert.Equal(owner.PlayerId, shape.OwnerId);
            owner.Status = PlayerMatchStatus.ELIMINATED;
            Assert.False(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            projectiles.ProcessSunCrossfires(runtime, shape.ArmedAtUtc);
            Assert.NotEmpty(runtime.SunCrossfireShapes);
            projectiles.ProcessSunCrossfires(runtime, shape.ExpiresAtUtc.AddSeconds(1));
            Assert.Empty(runtime.SunCrossfireShapes);
        }
    }

    [Fact]
    public void PersonalActivationRejectsAnotherPlayerInstanceAndMismatchedSunOwner()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948602);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var stale = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var attack = new ProximityCombatAttack(12, -100, AreaType.None, 107000010, 10);
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            Assert.Throws<InvalidOperationException>(() => attacks.ActivateOrbs(runtime, stale, now));
            Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(runtime, stale, attack, now));
            Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(runtime, owner, attack, now));
        }
    }
}
