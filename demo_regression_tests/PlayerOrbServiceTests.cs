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

            // 처음 본 오브는 계열과 무관하게 첫 위상만 심는다. 바람 주기가 짧아 바람이 먼저 든다.
            attacks.ActivateOrbs(runtime, owner, now);
            Assert.Equal(Config.MAX_HEALTH, victim.Health);
            Assert.Empty(runtime.PendingWaveAttacks);
            Assert.Empty(runtime.SunCrossfireShapes);
            var windReadyAt = owner.Orbs.GetNextOrbAttackAtUtc(orbs[windOrdinal].ItemUid)!.Value;
            Assert.True(windReadyAt > now);
            attacks.ActivateOrbs(runtime, owner, windReadyAt);
            Assert.True(victim.Health < Config.MAX_HEALTH);
            Assert.Empty(runtime.PendingWaveAttacks);
            Assert.False(owner.Orbs.IsOrbAttackReady(orbs[windOrdinal].ItemUid, windReadyAt));
            Assert.True(owner.Orbs.IsOrbAttackReady(orbs[windOrdinal].ItemUid,
                windReadyAt.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS)));
            var readyAt = owner.Orbs.GetNextOrbAttackAtUtc(orbs[waveOrdinal].ItemUid)!.Value;
            Assert.True(readyAt > windReadyAt);

            // 색상별로 순서를 다시 매기지 않고 전체 보유 순서의 위치에서 발동한다.
            var wavePosition = trails.GetOrbPosition(runtime, owner, waveOrdinal, owner.Position, tiers);
            victim.Position = wavePosition;
            attacks.ActivateOrbs(runtime, owner, readyAt);
            Assert.Single(runtime.PendingWaveAttacks);
            Assert.True(owner.Orbs.GetNextOrbAttackAtUtc(orbs[waveOrdinal].ItemUid) > readyAt);
            attacks.ActivateOrbs(runtime, owner, readyAt);
            Assert.Single(runtime.PendingWaveAttacks);
        }
    }

    [Fact]
    public void SunProjectilesOutliveOwner()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948601);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var projectiles = provider.GetRequiredService<MatchOrbAttackService>();
        var cell = MatchSpawnData.GetCorridorAnchor(1);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = position };
        var victim = new Player(new PlayerInfo { PlayerId = 12 }) { Position = new Vector3f(position.X + 1f, position.Y, 0f) };
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            runtime.RegisterPlayer(victim);
            owner.Orbs.AddOrb(107000010);
            // 첫 위상을 심은 뒤 다음 틱에서 사거리 안 상대에게 발동한다.
            attacks.ActivateOrbs(runtime, owner, now);
            Assert.Empty(runtime.SunCrossfireShapes);
            now = now.AddSeconds(3);
            attacks.ActivateOrbs(runtime, owner, now);
            var shape = Assert.Single(runtime.SunCrossfireShapes);
            Assert.Equal(owner.PlayerId, shape.OwnerId);
            Assert.Equal(area, shape.Area);

            // 탈락한 소유자는 더 쏘지 않지만 이미 나간 투사체는 끝까지 간다.
            owner.Status = PlayerMatchStatus.ELIMINATED;
            attacks.ActivateOrbs(runtime, owner, now.AddSeconds(3));
            Assert.Single(runtime.SunCrossfireShapes);
            projectiles.ProcessSunCrossfires(runtime, shape.ArmedAtUtc);
            Assert.NotEmpty(runtime.SunCrossfireShapes);
            projectiles.ProcessSunCrossfires(runtime, shape.ExpiresAtUtc.AddSeconds(1));
            Assert.Empty(runtime.SunCrossfireShapes);
        }
    }

    [Fact]
    public void PersonalActivationRejectsAnotherPlayerInstance()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948602);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var stale = new Player(new PlayerInfo { PlayerId = 11 }) { Position = new Vector3f() };
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            Assert.Throws<InvalidOperationException>(() => attacks.ActivateOrbs(runtime, stale, now));
        }
    }
}
