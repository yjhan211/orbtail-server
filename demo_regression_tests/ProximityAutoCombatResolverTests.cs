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
    public void Resolve_TargetPriorityWinsBeforeDistance()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010),
            Actor(-202001, 2f, 0f) with { TargetPriority = 1 },
            Actor(-202002, 0.5f, 0f) with { TargetPriority = 2 }
        };

        Assert.Empty(resolver.Resolve(202, actors, now));
        var attack = Assert.Single(resolver.Resolve(202, actors, now.Add(ProximityAutoCombatResolver.AimDuration)));

        Assert.Equal(-202001, attack.TargetPlayerId);
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

    [Fact]
    public void Resolve_WindProfileHitsPrimaryAndTwoAdditionalTargetsWithReducedDamage()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000020) with
            {
                MaxTargets = 3,
                AdditionalTargetDamageMultiplier = 0.5f
            },
            Actor(2, 2f, 0f),
            Actor(3, 1f, 0f),
            Actor(4, 2.5f, 0f)
        };

        Assert.Empty(resolver.Resolve(198, actors, now));
        var attacks = resolver.Resolve(198, actors, now.Add(ProximityAutoCombatResolver.AimDuration));

        Assert.Equal(new long[] { 3, 2, 4 }, attacks.Select(attack => attack.TargetPlayerId));
        Assert.Equal(new[] { 6, 3, 3 }, attacks.Select(attack => attack.Damage));
    }

    [Fact]
    public void Resolve_WaveProfileUsesThreeFastOpeningAttacksThenReturnsToBaseInterval()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000030) with
            {
                InitialBurstAttackCount = 3,
                InitialBurstAttackIntervalMultiplier = 0.4f,
                BurstRechargeSeconds = 3f
            },
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(198, actors, now));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(500)));
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(1099)));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(1100)));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(1700)));
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(3199)));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(3200)));
    }

    [Fact]
    public void Resolve_WaveBurstRechargesOnlyAfterZeroTargetsAndDoesNotReturnOnTargetChangeOrGraceReacquire()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000030) with
            {
                InitialBurstAttackCount = 3,
                InitialBurstAttackIntervalMultiplier = 0.4f,
                BurstRechargeSeconds = 3f
            },
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };

        Assert.Empty(resolver.Resolve(198, actors, now));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(500)));

        // Target 2 disappears but target 3 remains: this is a target change, not a recharge condition.
        actors[1] = Actor(2, 1f, 0f, area: AreaType.Corridor3F);
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(600)));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(1100)));
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(1700)));

        // Only now, with no valid target, does the three-second recharge start.
        actors[2] = Actor(3, 2f, 0f, area: AreaType.Corridor3F);
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(1800)));
        actors[2] = Actor(3, 2f, 0f);
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(2500)));
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(3299)));
        Assert.Single(resolver.Resolve(198, actors, now.AddMilliseconds(3300)));
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(3900)));
    }
    [Fact]
    public void Resolve_WindProfileChecksLineOfSightForEveryAdditionalTarget()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000020) with
            {
                MaxTargets = 3,
                AdditionalTargetDamageMultiplier = 0.5f
            },
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };
        static bool HasLineOfSight(ProximityCombatActor _, ProximityCombatActor target) =>
            target.PlayerId != 2;

        Assert.Empty(resolver.Resolve(198, actors, now, HasLineOfSight));
        var attacks = resolver.Resolve(
            198,
            actors,
            now.Add(ProximityAutoCombatResolver.AimDuration),
            HasLineOfSight);

        var attack = Assert.Single(attacks);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(6, attack.Damage);
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetWithinGraceResumesPausedAim()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(198, actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.Corridor3F);
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(300)));

        actors[1] = Actor(2, 1f, 0f);
        var reacquiredAt = now.AddMilliseconds(1200);
        Assert.Empty(resolver.Resolve(198, actors, reacquiredAt));
        Assert.Empty(resolver.Resolve(198, actors, reacquiredAt.AddMilliseconds(199)));
        Assert.Single(resolver.Resolve(198, actors, reacquiredAt.AddMilliseconds(200)));
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetAfterGraceRestartsAim()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(198, actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.Corridor3F);
        Assert.Empty(resolver.Resolve(198, actors, now.AddMilliseconds(300)));

        var reacquiredAt = now.AddMilliseconds(1801);
        actors[1] = Actor(2, 1f, 0f);
        Assert.Empty(resolver.Resolve(198, actors, reacquiredAt));
        Assert.Empty(resolver.Resolve(198, actors, reacquiredAt.AddMilliseconds(499)));
        Assert.Single(resolver.Resolve(198, actors, reacquiredAt.AddMilliseconds(500)));
    }

    [Fact]
    public void Resolve_PreservesSunAndWaveResonanceMetadataForServerProcResolution()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010) with
            {
                WeaponItemUid = 101,
                SunResonanceStage = 5,
                WaveResonanceArmed = true
            },
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(200, actors, now));
        var attack = Assert.Single(resolver.Resolve(200, actors, now.Add(ProximityAutoCombatResolver.AimDuration)));

        Assert.Equal(5, attack.SunResonanceStage);
        Assert.True(attack.WaveResonanceArmed);
        Assert.False(attack.IsResonanceProc);
    }
    [Fact]
    public void Resolve_MultipleOrbInstancesUseIndependentCooldownsAndOneTargetActor()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 24, 3, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010) with
            {
                WeaponItemUid = 101
            },
            Actor(1, 0f, 0f, weaponItemId: 107000020) with
            {
                WeaponItemUid = 102,
                AttackIntervalSeconds = 0.6f
            },
            Actor(2, 1f, 0f) with
            {
                WeaponItemUid = 201
            },
            Actor(2, 1f, 0f) with
            {
                WeaponItemUid = 202
            }
        };

        Assert.Empty(resolver.Resolve(200, actors, now));

        var openingAttacks = resolver.Resolve(
            200,
            actors,
            now.Add(ProximityAutoCombatResolver.AimDuration));
        Assert.Equal(2, openingAttacks.Count);
        Assert.All(openingAttacks, attack => Assert.Equal(2, attack.TargetPlayerId));

        var nextAttack = Assert.Single(resolver.Resolve(200, actors, now.AddMilliseconds(1100)));
        Assert.Equal(107000020, nextAttack.WeaponItemId);
    }

    [Fact]
    public void Resolve_MultipleOrbInstancesCanStaggerTheirOpeningVolley()
    {
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010) with
            {
                WeaponItemUid = 101,
                InitialAttackDelaySeconds = 0f
            },
            Actor(1, 0f, 0f, weaponItemId: 107000020) with
            {
                WeaponItemUid = 102,
                InitialAttackDelaySeconds = 0.15f
            },
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.Resolve(200, actors, now));
        Assert.Single(resolver.Resolve(200, actors, now.AddMilliseconds(500)));
        Assert.Empty(resolver.Resolve(200, actors, now.AddMilliseconds(649)));
        Assert.Single(resolver.Resolve(200, actors, now.AddMilliseconds(650)));
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
