using game_server.combat;
using game_server.matches;
using game_server.players;
using Microsoft.Extensions.DependencyInjection;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbAttackTests
{
    public PlayerOrbAttackTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void SunActivationCapsTelegraphsAndProjectilesOutliveOwner()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948601);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var projectiles = provider.GetRequiredService<SunOrbAttackService>();
        var cell = MatchSpawnData.GetCorridorAnchor(1);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = position, CurrentArea = area };
        var anchor = new Vector3f(position.X + 2, position.Y + 1, 0);
        var attack = new ProximityCombatAttack(11, -100, area, 107000010, 10, 1, 1,
            Origin: position, AnchorPosition: anchor);
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterParticipant(owner);
            for (int i = 0; i < Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER; i++)
                Assert.True(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            Assert.False(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            var shape = runtime.SunOrbAttacks.GetShapeAt(0);
            Assert.Equal(owner.PlayerId, shape.OwnerId);
            owner.Status = PlayerMatchStatus.ELIMINATED;
            Assert.False(attacks.TryStartSunCrossfire(runtime, owner, attack, now));
            projectiles.ProcessSwarmCrossfires(runtime, shape.ArmedAtUtc);
            Assert.True(runtime.SunOrbAttacks.ShapeCount > 0);
            projectiles.ProcessSwarmCrossfires(runtime, shape.ExpiresAtUtc.AddSeconds(1));
            Assert.Equal(0, runtime.SunOrbAttacks.ShapeCount);
        }
    }

    [Fact]
    public void PersonalActivationRejectsAnotherPlayerInstanceAndMismatchedSunOwner()
    {
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var runtime = provider.GetRequiredService<MatchRuntimeStore>().GetOrCreate(948602);
        var attacks = provider.GetRequiredService<PlayerOrbService>();
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f() };
        var stale = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f() };
        var attack = new ProximityCombatAttack(12, -100, AreaType.None, 107000010, 10, 1, 1);
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterParticipant(owner);
            Assert.Throws<InvalidOperationException>(() => attacks.ActivateWindOrbs(runtime, stale, now));
            Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(runtime, stale, attack, now));
            Assert.Throws<InvalidOperationException>(() => attacks.TryStartSunCrossfire(runtime, owner, attack, now));
        }
    }
}
