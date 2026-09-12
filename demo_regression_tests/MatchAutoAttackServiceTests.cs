using game_server.matches;
using game_server.matches.combat;
using game_server.players;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class MatchAutoAttackServiceTests
{
    /// <summary>조준 경계를 검증하는 단언은 상수를 기준으로 잡아야 값이 바뀌어도 의미가 유지된다.</summary>
    private static readonly double AimMs = MatchAutoAttackService.AimDuration.TotalMilliseconds;

    [Fact]
    public void MatchOwnedCombat_IsolatesCooldownsAndStopsAfterEnd()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var service = new MatchAutoAttackService();
        var first = store.GetOrCreate(501);
        var second = store.GetOrCreate(502);
        foreach (var match in new[] { first, second })
        {
            match.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = 1 } });
            match.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = 2 } });
        }
        var now = DateTime.UtcNow;
        var actors = new[] { Actor(1, 0, 0, 107000003), Actor(2, 1, 0) };
        using (MatchRuntimeStore.Enter(first))
        {
            Assert.Empty(service.UpdateAttacks(first, actors, now));
            Assert.Single(service.UpdateAttacks(first, actors, now.AddMilliseconds(AimMs)));
        }
        using (MatchRuntimeStore.Enter(second))
        {
            Assert.Empty(service.UpdateAttacks(second, actors, now.AddMilliseconds(AimMs)));
            Assert.Single(service.UpdateAttacks(second, actors, now.AddMilliseconds(AimMs * 2)));
        }
        using (MatchRuntimeStore.Enter(first))
        {
            first.GetParticipant(1)!.AutoAttack.ResetAttackCooldown(0, now.AddMilliseconds(AimMs * 2));
            Assert.Single(service.UpdateAttacks(first, actors, now.AddMilliseconds(AimMs * 2)));
            first.TryMarkEnded();
        }
        Assert.Null(store.GetOrNull(501));
        using (MatchRuntimeStore.Enter(first))
            Assert.Empty(service.UpdateAttacks(first, actors, now.AddSeconds(10)));
        using (MatchRuntimeStore.Enter(second))
            Assert.Empty(service.UpdateAttacks(second, actors, now.AddMilliseconds(AimMs * 3)));
    }

    [Fact]
    public void Resolve_ArmedActorTargetsNearestPlayerInRange()
    {
        using var arena = new Arena(910, 1, 2, 3);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 2f, 0f),
            Actor(3, 1f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));
        var attacks = arena.Update(actors, now.Add(MatchAutoAttackService.AimDuration));

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(107000003, attack.WeaponItemId);
    }

    [Fact]
    public void Resolve_UnarmedActorCanBeHitButDoesNotAttack()
    {
        using var arena = new Arena(911, 1, 2);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));
        var attacks = arena.Update(actors, now.Add(MatchAutoAttackService.AimDuration));

        var attack = Assert.Single(attacks);
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_RespectsAreaRangeAimAndAttackInterval()
    {
        using var arena = new Arena(912, 1, 2, 3);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 4f, 0f),
            Actor(3, 1f, 0f, area: AreaType.S2Corridor3)
        };

        Assert.Empty(arena.Update(actors, now));

        actors[1] = Actor(2, 2f, 0f);
        Assert.Empty(arena.Update(actors, now));
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(AimMs - 1)));
        Assert.Single(arena.Update(actors, now.AddMilliseconds(AimMs)));
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(AimMs + 1499)));
        Assert.Single(arena.Update(actors, now.AddMilliseconds(AimMs + 1500)));
    }

    /// <summary>
    ///     쿨다운 승계 (2026-08-24 연사 수리): 표적이 바뀌어도 같은 오브의 진행 중 쿨다운은
    ///     이어진다. 승계 전에는 표적 교체가 NextAttackAtUtc를 조준 시간(0.1초)으로 갈아치워,
    ///     한 발이 한 마리인 태양이 몹 무리 앞에서 티어 주기 대신 0.1초 연사를 했다.
    /// </summary>
    [Fact]
    public void Resolve_TargetSwitchInheritsPendingAttackCooldown()
    {
        using var arena = new Arena(913, 1, 2, 3);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));
        var firstShotAtUtc = now.AddMilliseconds(AimMs);
        Assert.Single(arena.Update(actors, firstShotAtUtc));

        // 첫 발 직후 표적이 죽고 더 가까운 새 표적이 나타난다 — 표적 교체.
        actors[1] = Actor(3, 0.5f, 0f);
        var retargetAtUtc = firstShotAtUtc.AddMilliseconds(50);
        Assert.Empty(arena.Update(actors, retargetAtUtc));

        // 조준(0.1초)이 끝나도 이전 발의 주기(1.5초)가 남아 있으면 쏘지 않는다.
        Assert.Empty(arena.Update(actors, retargetAtUtc.AddMilliseconds(AimMs)));
        Assert.Empty(arena.Update(actors, firstShotAtUtc.AddMilliseconds(1499)));

        var attack = Assert.Single(arena.Update(actors, firstShotAtUtc.AddMilliseconds(1500)));
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
        using var arena = new Arena(914, 1, 2, 3);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var attacker = Actor(1, 0f, 0f, weaponItemId: 107000003);
        var first = Actor(2, 1f, 0f);

        Assert.Empty(arena.Update([attacker, first], now));
        var firstShotAtUtc = now.AddMilliseconds(AimMs);
        Assert.Single(arena.Update([attacker, first], firstShotAtUtc));

        // 첫 발 직후 표적 전멸 — 상태가 유예로 빠진다.
        Assert.Empty(arena.Update([attacker], firstShotAtUtc.AddMilliseconds(100)));

        // 0.3초 뒤 '다른' 표적 등장 — 스폰 스트림 재현. 조준이 끝나도 이전 발의 주기가 남아 있다.
        var second = Actor(3, 1f, 0f);
        var reappearAtUtc = firstShotAtUtc.AddMilliseconds(300);
        Assert.Empty(arena.Update([attacker, second], reappearAtUtc));
        Assert.Empty(arena.Update([attacker, second], reappearAtUtc.AddMilliseconds(AimMs)));
        Assert.Empty(arena.Update([attacker, second], firstShotAtUtc.AddMilliseconds(1499)));

        var attack = Assert.Single(
            arena.Update([attacker, second], firstShotAtUtc.AddMilliseconds(1500)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsDeterministicTieBreaker()
    {
        using var arena = new Arena(915, 2, 3, 10);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(10, 0f, 0f, weaponItemId: 107000003),
            Actor(3, -1f, 0f),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));
        var attack = Assert.Single(arena.Update(actors, now.Add(MatchAutoAttackService.AimDuration)));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_PrefersEnemyPlayerOverCloserMonster()
    {
        using var arena = new Arena(916, 1, 2);
        var now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000010),
            Actor(2, 2f, 0f),
            Actor(-202001, 0.5f, 0f) with { IsMonsterTarget = true, TargetPriority = 2 }
        };

        Assert.Empty(arena.Update(actors, now));
        var attack = Assert.Single(arena.Update(actors, now.Add(MatchAutoAttackService.AimDuration)));

        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_KeepsCurrentMonsterAheadOfCloserMonster_ButPlayerPreemptsIt()
    {
        using var arena = new Arena(918, 1, 2);
        var now = new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);
        var attacker = Actor(1, 0f, 0f, weaponItemId: 107000010);
        var normal = Actor(-202002, 1f, 0f) with { IsMonsterTarget = true, TargetPriority = 2 };
        var closer = Actor(-202001, 0.25f, 0f) with { IsMonsterTarget = true, TargetPriority = 2 };

        Assert.Empty(arena.Update([attacker, normal], now));
        var monsterAttack = Assert.Single(arena.Update(
            [attacker, normal, closer],
            now.Add(MatchAutoAttackService.AimDuration)));
        Assert.Equal(normal.PlayerId, monsterAttack.TargetPlayerId);

        var enemyPlayer = Actor(2, 2f, 0f);
        Assert.Empty(arena.Update(
            [attacker, normal, closer, enemyPlayer],
            now.AddMilliseconds(750)));
        // 쿨다운 승계 (2026-08-24 연사 수리): 표적이 플레이어로 바뀌어도 첫 발(0.1초)의
        // 주기 1.5초가 이어진다 — 발사는 1.6초부터. 우선순위 검증(플레이어 선점)은 그대로다.
        Assert.Empty(arena.Update(
            [attacker, normal, closer, enemyPlayer],
            now.AddMilliseconds(1250)));
        var playerAttack = Assert.Single(arena.Update(
            [attacker, normal, closer, enemyPlayer],
            now.Add(MatchAutoAttackService.AimDuration).AddMilliseconds(1500)));

        Assert.Equal(enemyPlayer.PlayerId, playerAttack.TargetPlayerId);
    }
    [Fact]
    public void Resolve_TargetChangeRestartsAim()
    {
        using var arena = new Arena(919, 1, 2, 3);
        var now = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));

        actors[1] = Actor(2, 4f, 0f);
        actors[2] = Actor(3, 1f, 0f);
        // 타깃이 바뀐 시점부터 조준이 다시 시작된다.
        const double targetChangedAtMs = 250;
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(targetChangedAtMs)));
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(targetChangedAtMs + AimMs - 1)));

        var attack = Assert.Single(
            arena.Update(actors, now.AddMilliseconds(targetChangedAtMs + AimMs)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_SkipsCandidatesRejectedByCanAttackTarget()
    {
        using var arena = new Arena(923, 1, 2, 3);
        var now = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000020),
            Actor(2, 1f, 0f),
            Actor(3, 2f, 0f)
        };
        static bool CanAttackTarget(ProximityCombatActor _, ProximityCombatActor target) =>
            target.PlayerId != 2;

        Assert.Empty(arena.Update(actors, now, CanAttackTarget));
        var attacks = arena.Update(
            actors,
            now.Add(MatchAutoAttackService.AimDuration),
            CanAttackTarget);

        var attack = Assert.Single(attacks);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(6, attack.Damage);
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetWithinGraceResumesPausedAim()
    {
        using var arena = new Arena(924, 1, 2);
        var now = new DateTime(2026, 7, 22, 1, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        // 조준을 절반만 마친 상태에서 타깃을 잃고, 유예 안에 다시 잡으면 남은 절반만 채운다.
        double halfAimMs = AimMs / 2;
        Assert.Empty(arena.Update(actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(halfAimMs)));

        actors[1] = Actor(2, 1f, 0f);
        var reacquiredAt = now.AddMilliseconds(halfAimMs + 900);
        Assert.Empty(arena.Update(actors, reacquiredAt));
        Assert.Empty(arena.Update(actors, reacquiredAt.AddMilliseconds(halfAimMs - 1)));
        Assert.Single(arena.Update(actors, reacquiredAt.AddMilliseconds(halfAimMs)));
    }

    [Fact]
    public void Resolve_ReacquiringSameTargetAfterGraceRestartsAim()
    {
        using var arena = new Arena(925, 1, 2);
        var now = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        var actors = new[]
        {
            Actor(1, 0f, 0f, weaponItemId: 107000003),
            Actor(2, 1f, 0f)
        };

        Assert.Empty(arena.Update(actors, now));
        actors[1] = Actor(2, 1f, 0f, area: AreaType.S2Corridor3);
        Assert.Empty(arena.Update(actors, now.AddMilliseconds(300)));

        var reacquiredAt = now.AddMilliseconds(1801);
        actors[1] = Actor(2, 1f, 0f);
        Assert.Empty(arena.Update(actors, reacquiredAt));
        Assert.Empty(arena.Update(actors, reacquiredAt.AddMilliseconds(AimMs - 1)));
        Assert.Single(arena.Update(actors, reacquiredAt.AddMilliseconds(AimMs)));
    }

    [Fact]
    public void Resolve_MultipleOrbInstancesUseIndependentCooldownsAndOneTargetActor()
    {
        using var arena = new Arena(927, 1, 2);
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

        Assert.Empty(arena.Update(actors, now));

        var openingAttacks = arena.Update(
            actors,
            now.Add(MatchAutoAttackService.AimDuration));
        Assert.Equal(2, openingAttacks.Count);
        Assert.All(openingAttacks, attack => Assert.Equal(2, attack.TargetPlayerId));

        var nextAttack = Assert.Single(arena.Update(actors, now.AddMilliseconds(1100)));
        Assert.Equal(107000020, nextAttack.WeaponItemId);
    }

    /// <summary>매치 하나와 참가자를 등록하고 잠금을 잡은 채 서비스를 돌리는 테스트 무대.</summary>
    private sealed class Arena : IDisposable
    {
        private readonly IDisposable _lock;
        private readonly MatchAutoAttackService _service = new();

        public Arena(long matchingId, params long[] playerIds)
        {
            var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Match = store.GetOrCreate(matchingId);
            foreach (long playerId in playerIds)
            {
                Match.RegisterParticipant(new Player { Profile = new PlayerInfo { PlayerId = playerId } });
            }
            _lock = MatchRuntimeStore.Enter(Match);
        }

        public MatchRuntime Match { get; }

        public IReadOnlyList<ProximityCombatAttack> Update(IReadOnlyList<ProximityCombatActor> actors, DateTime nowUtc, Func<ProximityCombatActor, ProximityCombatActor, bool>? canAttackTarget = null) =>
            _service.UpdateAttacks(Match, actors, nowUtc, canAttackTarget);

        public void Dispose() => _lock.Dispose();
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
            armed ? 1.5f : 0f);
    }
}
