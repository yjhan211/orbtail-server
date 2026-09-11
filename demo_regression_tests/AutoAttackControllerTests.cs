using game_server.matches;
using game_server.matches.combat;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class AutoAttackControllerTests
{
    /// <summary>조준 경계를 검증하는 단언은 상수를 기준으로 잡아야 값이 바뀌어도 의미가 유지된다.</summary>
    private static readonly double AimMs = AutoAttackController.AimDuration.TotalMilliseconds;

    [Fact]
    public void MatchOwnedCombat_IsolatesCooldownsAndReleasesAllState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var first = store.GetOrCreate(501);
        var second = store.GetOrCreate(502);
        var now = DateTime.UtcNow;
        var actors = new[] { Actor(1, 0, 0, 107000003), Actor(2, 1, 0) };
        Assert.NotSame(first.AutoAttack, second.AutoAttack);
        using (MatchRuntimeStore.Enter(first))
        {
            Assert.Empty(first.AutoAttack.ResolveAttacks(501, actors, now));
            Assert.Single(first.AutoAttack.ResolveAttacks(501, actors, now.AddMilliseconds(AimMs)));
            Assert.Empty(first.AutoAttack.ResolveAttacks(502, actors, now.AddMilliseconds(AimMs)));
        }
        using (MatchRuntimeStore.Enter(second))
        {
            Assert.Empty(second.AutoAttack.ResolveAttacks(502, actors, now.AddMilliseconds(AimMs)));
            Assert.Single(second.AutoAttack.ResolveAttacks(502, actors, now.AddMilliseconds(AimMs * 2)));
        }
        using (MatchRuntimeStore.Enter(first))
        {
            first.AutoAttack.RefundAttack(501, 1, 0, now.AddMilliseconds(AimMs * 2));
            Assert.Single(first.AutoAttack.ResolveAttacks(501, actors, now.AddMilliseconds(AimMs * 2)));
            first.TryMarkEnded();
        }
        Assert.Null(store.GetOrNull(501));
        foreach (string field in new[] { "_combatStates", "_burstRechargeReadyAtUtc", "_recentlyLostCombatStates" })
        {
            var state = typeof(AutoAttackController).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first.AutoAttack)!;
            Assert.Equal(0, (int)state.GetType().GetProperty("Count")!.GetValue(state)!);
        }
        Assert.Empty(first.AutoAttack.ResolveAttacks(501, actors, now.AddSeconds(10)));
        using (MatchRuntimeStore.Enter(second))
            Assert.Empty(second.AutoAttack.ResolveAttacks(502, actors, now.AddMilliseconds(AimMs * 3)));
    }

    [Fact]
    public void ClearRestartsAimWithoutReleasingTheMatch()
    {
        var resolver = new AutoAttackController(100);
        var now = DateTime.UtcNow;
        var actors = new[] { Actor(1, 0, 0, 107000003), Actor(2, 1, 0) };
        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        Assert.Single(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs)));
        resolver.Clear();
        Assert.Empty(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs)));
        Assert.Single(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs * 2)));
    }

    [Fact]
    public void Resolve_ArmedActorTargetsNearestPlayerInRange()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 2f, 0f),
            Actor(3, 1f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        var attacks = resolver.ResolveAttacks(100, actors, now.Add(AutoAttackController.AimDuration));

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
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        var attacks = resolver.ResolveAttacks(100, actors, now.Add(AutoAttackController.AimDuration));

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_RespectsAreaRangeAimAndAttackInterval()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 4f, 0f),
            Actor(3, 1f, 0f, area: AreaType.S2Corridor3)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));

        actors[1] = Actor(2, 2f, 0f);
        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        Assert.Empty(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs - 1)));
        Assert.Single(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs)));
        Assert.Empty(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs + 1499)));
        Assert.Single(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(AimMs + 1500)));
    }

    /// <summary>
    ///     쿨다운 승계 (2026-08-24 연사 수리): 표적이 바뀌어도 같은 오브의 진행 중 쿨다운은
    ///     이어진다. 승계 전에는 표적 교체가 NextAttackAtUtc를 조준 시간(0.1초)으로 갈아치워,
    ///     한 발이 한 마리인 태양이 몹 무리 앞에서 티어 주기 대신 0.1초 연사를 했다.
    /// </summary>
    [Fact]
    public void Resolve_TargetSwitchInheritsPendingAttackCooldown()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        var firstShotAtUtc = now.AddMilliseconds(AimMs);
        Assert.Single(resolver.ResolveAttacks(100, actors, firstShotAtUtc));

        // 첫 발 직후 표적이 죽고 더 가까운 새 표적이 나타난다 — 표적 교체.
        actors[1] = Actor(3, 0.5f, 0f);
        var retargetAtUtc = firstShotAtUtc.AddMilliseconds(50);
        Assert.Empty(resolver.ResolveAttacks(100, actors, retargetAtUtc));

        // 조준(0.1초)이 끝나도 이전 발의 주기(1.5초)가 남아 있으면 쏘지 않는다.
        Assert.Empty(resolver.ResolveAttacks(100, actors, retargetAtUtc.AddMilliseconds(AimMs)));
        Assert.Empty(resolver.ResolveAttacks(100, actors, firstShotAtUtc.AddMilliseconds(1499)));

        var attack = Assert.Single(resolver.ResolveAttacks(100, actors, firstShotAtUtc.AddMilliseconds(1500)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    /// <summary>
    ///     쿨다운 승계 2 (2026-08-24 연사 수리 2탄): 표적이 전멸해 상태가 유예로 빠진 뒤 "다른"
    ///     표적으로 복귀해도 무기 쿨다운은 이어진다. 승계 전에는 스폰 스트림에서 발마다 표적
    ///     고갈·재등장이 반복되며 조준 시간(0.1초) 연발이 났다 (실측 0.2~0.3초 간격 3연발).
    /// </summary>
    [Fact]
    public void Resolve_TargetDroughtThenNewTargetInheritsPendingCooldown()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var attacker = Actor(1, 0f, 0f, weaponItemId: 107000003);
        var first = Actor(2, 1f, 0f);

        Assert.Empty(resolver.ResolveAttacks(100, [attacker, first], now));
        var firstShotAtUtc = now.AddMilliseconds(AimMs);
        Assert.Single(resolver.ResolveAttacks(100, [attacker, first], firstShotAtUtc));

        // 첫 발 직후 표적 전멸 — 상태가 유예로 빠진다.
        Assert.Empty(resolver.ResolveAttacks(100, [attacker], firstShotAtUtc.AddMilliseconds(100)));

        // 0.3초 뒤 '다른' 표적 등장 — 스폰 스트림 재현. 조준이 끝나도 이전 발의 주기가 남아 있다.
        var second = Actor(3, 1f, 0f);
        var reappearAtUtc = firstShotAtUtc.AddMilliseconds(300);
        Assert.Empty(resolver.ResolveAttacks(100, [attacker, second], reappearAtUtc));
        Assert.Empty(resolver.ResolveAttacks(100, [attacker, second], reappearAtUtc.AddMilliseconds(AimMs)));
        Assert.Empty(resolver.ResolveAttacks(100, [attacker, second], firstShotAtUtc.AddMilliseconds(1499)));

        var attack = Assert.Single(
            resolver.ResolveAttacks(100, [attacker, second], firstShotAtUtc.AddMilliseconds(1500)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsDeterministicTieBreaker()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(10, 0f, 0f, weaponItemId: 107000003),
            Actor(3, -1f, 0f),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));
        var attack = Assert.Single(resolver.ResolveAttacks(100, actors, now.Add(AutoAttackController.AimDuration)));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_PrefersEnemyPlayerOverCloserMonster()
    {
        var resolver = new AutoAttackController(202);
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010),
            Actor(2, 2f, 0f),
            Actor(-202001, 0.5f, 0f) with { IsMonsterTarget = true }
        };

        Assert.Empty(resolver.ResolveAttacks(202, actors, now));
        var attack = Assert.Single(resolver.ResolveAttacks(202, actors, now.Add(AutoAttackController.AimDuration)));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_KeepsCurrentCoreAheadOfOtherMonsters()
    {
        var resolver = new AutoAttackController(210);
        var now = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);
        var attacker = Actor(1, 0f, 0f, weaponItemId: 107000010);
        var core = Actor(-202001, 1f, 0f) with
        {
            IsMonsterTarget = true,
            IsCoreMonsterTarget = true
        };

        Assert.Empty(resolver.ResolveAttacks(210, [attacker, core], now));
        var normal = Actor(-202002, 0.25f, 0f) with { IsMonsterTarget = true };
        var attack = Assert.Single(resolver.ResolveAttacks(
            210,
            [attacker, core, normal],
            now.Add(AutoAttackController.AimDuration)));

        Assert.Equal(core.PlayerId, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_KeepsCurrentNormalAheadOfOtherMonsters_ButPlayerPreemptsIt()
    {
        var resolver = new AutoAttackController(211);
        var now = new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);
        var attacker = Actor(1, 0f, 0f, weaponItemId: 107000010);
        var normal = Actor(-202002, 1f, 0f) with { IsMonsterTarget = true };
        var core = Actor(-202001, 0.25f, 0f) with
        {
            IsMonsterTarget = true,
            IsCoreMonsterTarget = true
        };

        Assert.Empty(resolver.ResolveAttacks(211, [attacker, normal], now));
        var monsterAttack = Assert.Single(resolver.ResolveAttacks(
            211,
            [attacker, normal, core],
            now.Add(AutoAttackController.AimDuration)));
        Assert.Equal(normal.PlayerId, monsterAttack.TargetPlayerId);

        var enemyPlayer = Actor(2, 2f, 0f);
        Assert.Empty(resolver.ResolveAttacks(
            211,
            [attacker, normal, core, enemyPlayer],
            now.AddMilliseconds(750)));
        // 쿨다운 승계 (2026-08-24 연사 수리): 표적이 플레이어로 바뀌어도 첫 발(0.1초)의
        // 주기 1.5초가 이어진다 — 발사는 1.6초부터. 우선순위 검증(플레이어 선점)은 그대로다.
        Assert.Empty(resolver.ResolveAttacks(
            211,
            [attacker, normal, core, enemyPlayer],
            now.AddMilliseconds(1250)));
        var playerAttack = Assert.Single(resolver.ResolveAttacks(
            211,
            [attacker, normal, core, enemyPlayer],
            now.Add(AutoAttackController.AimDuration).AddMilliseconds(1500)));

        Assert.Equal(enemyPlayer.PlayerId, playerAttack.TargetPlayerId);
    }
    [Fact]
    public void Resolve_TargetChangeRestartsAim()
    {
        var resolver = new AutoAttackController(100);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(100, actors, now));

        actors[1] = Actor(2, 4f, 0f);
        actors[2] = Actor(3, 1f, 0f);
        // 타깃이 바뀐 시점부터 조준이 다시 시작된다.
        const double targetChangedAtMs = 250;
        Assert.Empty(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(targetChangedAtMs)));
        Assert.Empty(resolver.ResolveAttacks(100, actors, now.AddMilliseconds(targetChangedAtMs + AimMs - 1)));

        var attack = Assert.Single(
            resolver.ResolveAttacks(100, actors, now.AddMilliseconds(targetChangedAtMs + AimMs)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_WindProfileHitsPrimaryAndTwoAdditionalTargetsWithReducedDamage()
    {
        var resolver = new AutoAttackController(198);
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

        Assert.Empty(resolver.ResolveAttacks(198, actors, now));
        var attacks = resolver.ResolveAttacks(198, actors, now.Add(AutoAttackController.AimDuration));

        Assert.Equal(new long[] { 3, 2, 4 }, attacks.Select(attack => attack.TargetPlayerId));
        Assert.Equal(new[] { 6, 3, 3 }, attacks.Select(attack => attack.Damage));
    }

    [Fact]
    public void Resolve_WaveProfileUsesThreeFastOpeningAttacksThenReturnsToBaseInterval()
    {
        var resolver = new AutoAttackController(198);
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

        Assert.Empty(resolver.ResolveAttacks(198, actors, now));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(500)));
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1099)));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1100)));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1700)));
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(3199)));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(3200)));
    }

    [Fact]
    public void Resolve_WaveBurstRechargesOnlyAfterZeroTargetsAndDoesNotReturnOnTargetChangeOrGraceReacquire()
    {
        var resolver = new AutoAttackController(198);
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

        Assert.Empty(resolver.ResolveAttacks(198, actors, now));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(500)));

        // Target 2 disappears but target 3 remains: this is a target change, not a recharge condition.
        actors[1] = Actor(2, 1f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(600)));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1100)));
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1700)));

        // Only now, with no valid target, does the three-second recharge start.
        actors[2] = Actor(3, 2f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(1800)));
        actors[2] = Actor(3, 2f, 0f);
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(2500)));
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(3299)));
        Assert.Single(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(3300)));
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(3900)));
    }
    [Fact]
    public void Resolve_WindProfileChecksLineOfSightForEveryAdditionalTarget()
    {
        var resolver = new AutoAttackController(198);
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

        Assert.Empty(resolver.ResolveAttacks(198, actors, now, HasLineOfSight));
        var attacks = resolver.ResolveAttacks(
            198,
            actors,
            now.Add(AutoAttackController.AimDuration),
            HasLineOfSight);

        var attack = Assert.Single(attacks);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(6, attack.Damage);
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetWithinGraceResumesPausedAim()
    {
        var resolver = new AutoAttackController(198);
        var now = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        // 조준을 절반만 마친 상태에서 타깃을 잃고, 유예 안에 다시 잡으면 남은 절반만 채운다.
        double halfAimMs = AimMs / 2;
        Assert.Empty(resolver.ResolveAttacks(198, actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(halfAimMs)));

        actors[1] = Actor(2, 1f, 0f);
        var reacquiredAt = now.AddMilliseconds(halfAimMs + 900);
        Assert.Empty(resolver.ResolveAttacks(198, actors, reacquiredAt));
        Assert.Empty(resolver.ResolveAttacks(198, actors, reacquiredAt.AddMilliseconds(halfAimMs - 1)));
        Assert.Single(resolver.ResolveAttacks(198, actors, reacquiredAt.AddMilliseconds(halfAimMs)));
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetAfterGraceRestartsAim()
    {
        var resolver = new AutoAttackController(198);
        var now = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(resolver.ResolveAttacks(198, actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(resolver.ResolveAttacks(198, actors, now.AddMilliseconds(300)));

        var reacquiredAt = now.AddMilliseconds(1801);
        actors[1] = Actor(2, 1f, 0f);
        Assert.Empty(resolver.ResolveAttacks(198, actors, reacquiredAt));
        Assert.Empty(resolver.ResolveAttacks(198, actors, reacquiredAt.AddMilliseconds(AimMs - 1)));
        Assert.Single(resolver.ResolveAttacks(198, actors, reacquiredAt.AddMilliseconds(AimMs)));
    }

    [Fact]
    public void Resolve_PreservesSunAndWaveResonanceMetadataForServerProcResolution()
    {
        var resolver = new AutoAttackController(200);
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

        Assert.Empty(resolver.ResolveAttacks(200, actors, now));
        var attack = Assert.Single(resolver.ResolveAttacks(200, actors, now.Add(AutoAttackController.AimDuration)));

        Assert.Equal(5, attack.SunResonanceStage);
        Assert.True(attack.WaveResonanceArmed);
        Assert.False(attack.IsResonanceProc);
    }
    [Fact]
    public void Resolve_MultipleOrbInstancesUseIndependentCooldownsAndOneTargetActor()
    {
        var resolver = new AutoAttackController(200);
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

        Assert.Empty(resolver.ResolveAttacks(200, actors, now));

        var openingAttacks = resolver.ResolveAttacks(
            200,
            actors,
            now.Add(AutoAttackController.AimDuration));
        Assert.Equal(2, openingAttacks.Count);
        Assert.All(openingAttacks, attack => Assert.Equal(2, attack.TargetPlayerId));

        var nextAttack = Assert.Single(resolver.ResolveAttacks(200, actors, now.AddMilliseconds(1100)));
        Assert.Equal(107000020, nextAttack.WeaponItemId);
    }

    [Fact]
    public void Resolve_MultipleOrbInstancesCanStaggerTheirOpeningVolley()
    {
        var resolver = new AutoAttackController(200);
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

        // 슬롯 시차는 이 테스트가 직접 지정하므로 조준 시간에만 상대적으로 잡는다.
        Assert.Empty(resolver.ResolveAttacks(200, actors, now));
        Assert.Single(resolver.ResolveAttacks(200, actors, now.AddMilliseconds(AimMs)));
        Assert.Empty(resolver.ResolveAttacks(200, actors, now.AddMilliseconds(AimMs + 149)));
        Assert.Single(resolver.ResolveAttacks(200, actors, now.AddMilliseconds(AimMs + 150)));
    }
    private static ProximityCombatActor Actor(
        long playerId,
        float x,
        float y,
        int weaponItemId = 0,
        AreaType area = AreaType.S2Classroom1)
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
