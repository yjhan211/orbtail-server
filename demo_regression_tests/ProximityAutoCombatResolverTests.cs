using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class ProximityAutoCombatResolverTests
{
    [Fact]
    public void Resolve_ArmedActorTargetsNearestPlayerInRange()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 2f, 0f),
            Actor(3, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(100, actors, now));
        var attacks = resolver.Resolve(100, actors, now.Add(ProximityAutoCombatResolver.AimDuration));

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(107000003, attack.WeaponItemId);
        Assert.Equal(0.25f, attack.ProjectileWidth);
        Assert.Equal(1.5f, attack.EffectDurationSeconds);
    }

    [Fact]
    public void Resolve_UnarmedActorCanBeHitButDoesNotAttack()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(100, actors, now));
        var attacks = resolver.Resolve(100, actors, now.Add(ProximityAutoCombatResolver.AimDuration));

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_RespectsAreaRangeAimAndAttackInterval()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 4f, 0f),
            Actor(3, 1f, 0f, area: AreaType.Corridor3F)
        };

        Assert.Empty(resolver.Resolve(100, actors, now));

        actors[1] = Actor(2, 2f, 0f);
        Assert.Empty(resolver.Resolve(100, actors, now));
        Assert.Empty(resolver.Resolve(100, actors, now.AddMilliseconds(499)));
        Assert.Single(resolver.Resolve(100, actors, now.AddMilliseconds(500)));
        Assert.Empty(resolver.Resolve(100, actors, now.AddMilliseconds(1999)));
        Assert.Single(resolver.Resolve(100, actors, now.AddSeconds(2)));
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsDeterministicTieBreaker()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(10, 0f, 0f, weaponItemId: 107000003),
            Actor(3, -1f, 0f),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(100, actors, now));
        var attack = Assert.Single(resolver.Resolve(100, actors, now.Add(ProximityAutoCombatResolver.AimDuration)));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_TargetChangeRestartsHalfSecondAim()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };

        Assert.Empty(resolver.Resolve(100, actors, now));

        actors[1] = Actor(2, 4f, 0f);
        actors[2] = Actor(3, 1f, 0f);
        Assert.Empty(resolver.Resolve(100, actors, now.AddMilliseconds(250)));
        Assert.Empty(resolver.Resolve(100, actors, now.AddMilliseconds(500)));

        var attack = Assert.Single(resolver.Resolve(100, actors, now.AddMilliseconds(750)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    private static ProximityCombatActor Actor(
        long playerId,
        float x,
        float y,
        int weaponItemId = 0,
        AreaType area = AreaType.Classroom3)
    {
        bool armed = weaponItemId > 0;
        return new ProximityCombatActor(
            playerId,
            area,
            new Vector3f(x, y, 0f),
            weaponItemId,
            armed ? 3f : 0f,
            armed ? 6 : 0,
            armed ? 1.5f : 0f,
            armed ? 0.25f : 0f,
            armed ? 1.5f : 0f);
    }
}
