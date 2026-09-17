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
        var orbAttacks = provider.GetRequiredService<MatchOrbAttackService>();
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
            Assert.Empty(runtime.PendingSunAttacks);
            var windReadyAt = owner.Orbs.GetNextOrbAttackAtUtc(orbs[windOrdinal].ItemUid)!.Value;
            Assert.True(windReadyAt > now);
            attacks.ActivateOrbs(runtime, owner, windReadyAt);
            orbAttacks.ProcessWindAttacks(runtime, windReadyAt);
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

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(-1f, -1f)]
    [InlineData(-1f, 1f)]
    [InlineData(1f, -1f)]
    public void SunSelectsCellAxisNearestAnchor(float directionX, float directionY)
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948604);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var trails = provider.GetRequiredService<PlayerOrbTrailService>();
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, MatchSpawnData.GetCorridorAnchor(1));
        var owner = new Player(new PlayerInfo { PlayerId = 11 }) { Position = position };
        var victim = new Player(new PlayerInfo { PlayerId = 12 }) { Position = position };
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterPlayer(owner);
            runtime.RegisterPlayer(victim);
            owner.Orbs.AddOrb(107000010);
            var origin = trails.GetOrbPosition(runtime, owner, 0, position, PlayerOrbTrailService.GetOrbTiersInOrder(runtime, owner));
            var originCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, origin);
            int stepX = directionX == directionY ? (int)directionX : 0;
            int stepY = directionX == directionY ? 0 : (int)directionY;
            victim.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                new Cell(originCell.X + stepX, originCell.Y + stepY));
            attacks.ActivateOrbs(runtime, owner, now);
            attacks.ActivateOrbs(runtime, owner, now.AddSeconds(3));
            var shape = Assert.Single(runtime.PendingSunAttacks);
            Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, originCell), shape.Origin);
            Assert.Equal(Math.Sign(directionX), Math.Sign(shape.End.X - shape.Origin.X));
            Assert.Equal(Math.Sign(directionY), Math.Sign(shape.End.Y - shape.Origin.Y));
            float dx = Math.Abs(shape.End.X - shape.Origin.X);
            float dy = Math.Abs(shape.End.Y - shape.Origin.Y) * GroundGeometry.GroundYScale;
            Assert.InRange(Math.Abs(dx - dy), 0f, 0.001f);
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
            Assert.Empty(runtime.PendingSunAttacks);
            now = now.AddSeconds(3);
            attacks.ActivateOrbs(runtime, owner, now);
            var shape = Assert.Single(runtime.PendingSunAttacks);
            Assert.Equal(owner.PlayerId, shape.OwnerId);
            Assert.Equal(area, shape.Area);

            // 선은 셀 축을 따라 구역이 바뀌는 셀 면에서 끝난다 — 끝점 바로 안쪽은 같은 구역, 바로 바깥은 다른 구역.
            int stepX = Math.Sign(shape.EndCell.X - shape.OriginCell.X);
            int stepY = Math.Sign(shape.EndCell.Y - shape.OriginCell.Y);
            var outsideCell = new Cell(shape.EndCell.X + stepX, shape.EndCell.Y + stepY);
            Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, shape.EndCell), shape.End);
            Assert.Equal(area, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, shape.EndCell));
            Assert.NotEqual(area, GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, outsideCell));
            Assert.True(shape.GroundLength <= Config.SWARM_SUN_MAX_GROUND_LENGTH);

            // 탈락한 소유자는 더 쏘지 않지만 이미 나간 투사체는 끝까지 간다.
            owner.Status = PlayerMatchStatus.ELIMINATED;
            attacks.ActivateOrbs(runtime, owner, now.AddSeconds(3));
            Assert.Single(runtime.PendingSunAttacks);
            projectiles.ProcessSunAttacks(runtime, shape.ArmedAtUtc);
            Assert.NotEmpty(runtime.PendingSunAttacks);
            projectiles.ProcessSunAttacks(runtime, shape.ExpiresAtUtc.AddSeconds(1));
            Assert.Empty(runtime.PendingSunAttacks);
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
