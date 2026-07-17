using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class ProximityAutoCombatResolverTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void Resolve_ArmedActorTargetsNearestPlayerInRange()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 201000015),
            Actor(2, 2f, 0f),
            Actor(3, 1f, 0f)
        };

        var attacks = resolver.Resolve(100, actors, now, 3f, Cooldown);

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(201000015, attack.WeaponItemId);
    }

    [Fact]
    public void Resolve_UnarmedActorCanBeHitButDoesNotAttack()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 201000016),
            Actor(2, 1f, 0f)
        };

        var attacks = resolver.Resolve(100, actors, now, 3f, Cooldown);

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_RespectsAreaRangeAndCooldown()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 201000017),
            Actor(2, 4f, 0f),
            Actor(3, 1f, 0f, area: AreaType.Corridor3F)
        };

        Assert.Empty(resolver.Resolve(100, actors, now, 3f, Cooldown));

        actors[1] = Actor(2, 2f, 0f);
        Assert.Single(resolver.Resolve(100, actors, now, 3f, Cooldown));
        Assert.Empty(resolver.Resolve(100, actors, now.AddSeconds(1), 3f, Cooldown));
        Assert.Single(resolver.Resolve(100, actors, now.AddSeconds(1.5), 3f, Cooldown));
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsDeterministicTieBreaker()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(10, 0f, 0f, weaponItemId: 201000017),
            Actor(3, -1f, 0f),
            Actor(2, 1f, 0f)
        };

        var attack = Assert.Single(resolver.Resolve(100, actors, now, 3f, Cooldown));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    private static ProximityCombatActor Actor(
        long playerId,
        float x,
        float y,
        int weaponItemId = 0,
        AreaType area = AreaType.Classroom3)
    {
        return new ProximityCombatActor(playerId, area, new Vector3f(x, y, 0f), weaponItemId);
    }
}
