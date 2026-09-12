using game_server.matches;
using game_server.matches.combat;
using game_server.players;
using Microsoft.Extensions.DependencyInjection;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbServiceTests
{
    public PlayerOrbServiceTests() => TestGameData.EnsureBattleItemCombatLoaded();

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
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = position, CurrentArea = area };
        var anchor = new Vector3f(position.X + 2, position.Y + 1, 0);
        var attack = new ProximityCombatAttack(11, -100, area, 107000010, 10,
            Origin: position, AnchorPosition: anchor);
        var now = DateTime.UtcNow;
        using (runtime.Enter())
        {
            runtime.RegisterParticipant(owner);
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
        var owner = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f() };
        var stale = new Player { Profile = new PlayerInfo { PlayerId = 11 }, Position = new Vector3f() };
        var attack = new ProximityCombatAttack(12, -100, AreaType.None, 107000010, 10);
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
