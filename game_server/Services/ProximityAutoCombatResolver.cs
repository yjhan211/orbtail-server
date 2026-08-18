using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

public readonly record struct ProximityCombatActor(
    long PlayerId,
    AreaType Area,
    Vector3f Position,
    int WeaponItemId,
    float AttackRange,
    int Damage,
    float AttackIntervalSeconds,
    float ProjectileWidth = 0f,
    float EffectDurationSeconds = 0f,
    MapId MapId = MapId.None,
    Cell? Cell = null,
    int MaxTargets = 1,
    float AdditionalTargetDamageMultiplier = 1f,
    int InitialBurstAttackCount = 0,
    float InitialBurstAttackIntervalMultiplier = 1f,
    float BurstRechargeSeconds = 0f,
    bool OrbEffectActive = false,
    long WeaponItemUid = 0,
    int WeaponStackIndex = 0,
    int SunResonanceStage = 0,
    bool WaveResonanceArmed = false,
    float InitialAttackDelaySeconds = 0f,
    bool IsMonsterTarget = false,
    bool IsCoreMonsterTarget = false,
    int TargetPriority = -1,
    // #226 재개편: 발사 원점 전용 액터(오브) — 표적 후보에서 제외된다.
    bool Untargetable = false,
    // 오브열에서 몇 번째인가 (0 = 머리). 스웜 PvP가 "앞열 N개만 사람을 쏜다"를
    // 판정하는 근거 — WeaponStackIndex는 인벤토리 스택 순번이라 열 순서와 다르다.
    int TrailOrdinal = 0);

public readonly record struct ProximityCombatAttack(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId,
    int Damage,
    float ProjectileWidth,
    float EffectDurationSeconds,
    int CandidateTargetCount = 0,
    int SunResonanceStage = 0,
    bool WaveResonanceArmed = false,
    bool IsResonanceProc = false,
    bool IsWaveAreaAttack = false,
    bool IsWaveAreaSecondary = false,
    bool IsWindAreaAttack = false,
    bool IsWindAreaSecondary = false,
    // #232 1단계 기준점 잠금: 발사 순간의 발사 원점(오브 월드 좌표)과 표적 위치를 박제한다.
    // 교차사격(2단계) 모양은 이 두 점으로 방향·크기를 정하고, 예고 뒤 몬스터가 죽어도
    // 잠근 위치에서 끝까지 처리한다. 레거시 생성 경로는 null이라 종전과 같다.
    long AttackerItemUid = 0,
    Vector3f? Origin = null,
    Vector3f? AnchorPosition = null,
    // 발사한 오브의 열 순번 — 클라가 실제로 그리는 오브 슬롯에 예고의 시작점을 붙이는 근거.
    int AttackerTrailOrdinal = 0);

public readonly record struct ProximityCombatTargetEvent(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId,
    int TargetWeaponItemId,
    DateTimeOffset OccurredAtUtc,
    string Reason);

/// <summary>
///     Selects the nearest target(s) per armed actor while keeping attack cadence server-authoritative.
///     Damage application stays outside this class so every volley is selected from one shared snapshot.
/// </summary>
public sealed class ProximityAutoCombatResolver
{
    /// <summary>
    ///     조준 지연. 다른 구역 대상에게 "알 수 없는 피해"가 들어가던 문제를 임시로 덮으려고
    ///     2026-07-18에 500ms로 넣었으나, 다음 날 #194가 지역 경계와 벽 너머를 직접 차단하고
    ///     #206이 진입 직후 사각까지 막으면서 그 역할은 끝났다.
    ///     남은 것은 조우 반응이 굼뜨다는 체감뿐이라 100ms로 줄인다.
    ///     0으로 두지 않는 이유는 재획득 유예가 이어받을 조준 시간을 잃기 때문이다.
    /// </summary>
    public static readonly TimeSpan AimDuration = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan TargetReacquireGraceDuration = TimeSpan.FromSeconds(1.5);

    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId, long ItemUid, int StackIndex), CombatState>
        _combatStates = new();
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId, long ItemUid, int StackIndex), DateTime>
        _burstRechargeReadyAtUtc = new();
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId, long ItemUid, int StackIndex),
        SuspendedCombatState>
        _recentlyLostCombatStates = new();

    public IReadOnlyList<ProximityCombatAttack> Resolve(
        long matchingId,
        IReadOnlyList<ProximityCombatActor> actors,
        DateTime nowUtc,
        Func<ProximityCombatActor, ProximityCombatActor, bool>? hasLineOfSight = null,
        Action<ProximityCombatTargetEvent>? onTargetAcquired = null,
        Action<ProximityCombatTargetEvent>? onTargetLost = null)
    {
        if (matchingId <= 0)
            return [];

        var attacks = new List<ProximityCombatAttack>();
        var activeAttackers = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();

        foreach (var attacker in actors)
        {
            var attackerKey = (attacker.PlayerId, attacker.WeaponItemUid, attacker.WeaponStackIndex);
            var stateKey = (matchingId, attacker.PlayerId, attacker.WeaponItemUid, attacker.WeaponStackIndex);
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None ||
                attacker.AttackRange <= 0f || attacker.Damage <= 0 || attacker.AttackIntervalSeconds <= 0f)
            {
                if (_combatStates.TryRemove(stateKey, out var previousState))
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId, previousState, nowUtc, "attacker_unarmed"));
                _burstRechargeReadyAtUtc.TryRemove(stateKey, out _);
                _recentlyLostCombatStates.TryRemove(stateKey, out _);
                continue;
            }

            if (attacker.InitialBurstAttackCount <= 0)
                _burstRechargeReadyAtUtc.TryRemove(stateKey, out _);

            activeAttackers.Add(attackerKey);
            if (_recentlyLostCombatStates.TryGetValue(stateKey, out var expiredState) &&
                nowUtc - expiredState.LostAtUtc > TargetReacquireGraceDuration)
            {
                _recentlyLostCombatStates.TryRemove(stateKey, out _);
            }
            float attackRangeSquared = attacker.AttackRange * attacker.AttackRange;
            var eligibleTargetsByPlayer =
                new Dictionary<long, (ProximityCombatActor Actor, float DistanceSquared)>();

            foreach (var candidate in actors)
            {
                if (candidate.Untargetable ||
                    candidate.PlayerId == attacker.PlayerId || candidate.Area != attacker.Area)
                    continue;

                float dx = attacker.Position.X - candidate.Position.X;
                float dy = attacker.Position.Y - candidate.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                // 몹·플레이어 사거리 통일 (2026-08-13 유저 결정): 몹 사거리 무시 특례 퇴역 —
                // 색 사거리 30 통일로 특례 없이도 구역 전체가 커버된다.
                if (distanceSquared > attackRangeSquared)
                    continue;
                if (hasLineOfSight != null && !hasLineOfSight(attacker, candidate))
                    continue;

                if (!eligibleTargetsByPlayer.TryGetValue(candidate.PlayerId, out var existing) ||
                    distanceSquared < existing.DistanceSquared ||
                    distanceSquared.Equals(existing.DistanceSquared) &&
                    CompareWeaponInstance(candidate, existing.Actor) < 0)
                {
                    eligibleTargetsByPlayer[candidate.PlayerId] = (candidate, distanceSquared);
                }
            }

            var eligibleTargets = eligibleTargetsByPlayer.Values.ToList();

            if (eligibleTargets.Count == 0)
            {
                if (_combatStates.TryRemove(stateKey, out var previousState))
                {
                    _recentlyLostCombatStates[stateKey] = new SuspendedCombatState(previousState, nowUtc);
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId, previousState, nowUtc, "out_of_range_or_los"));
                    if (attacker.InitialBurstAttackCount > 0 && attacker.BurstRechargeSeconds > 0f)
                        _burstRechargeReadyAtUtc[stateKey] =
                            nowUtc.AddSeconds(attacker.BurstRechargeSeconds);
                }
                continue;
            }

            bool hasCombatState = _combatStates.TryGetValue(stateKey, out var combatState);
            eligibleTargets.Sort((left, right) =>
                CompareTargetPriority(left, right, hasCombatState ? combatState.TargetPlayerId : 0));
            var nearestTarget = eligibleTargets[0].Actor;
            if (!hasCombatState ||
                combatState.TargetPlayerId != nearestTarget.PlayerId ||
                combatState.WeaponItemId != attacker.WeaponItemId)
            {
                if (hasCombatState)
                {
                    string reason = combatState.TargetPlayerId != nearestTarget.PlayerId
                        ? "target_changed"
                        : "weapon_changed";
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId,
                        combatState,
                        nowUtc,
                        reason));
                    _recentlyLostCombatStates.TryRemove(stateKey, out _);
                }

                CombatState nextCombatState;
                if (!hasCombatState &&
                    _recentlyLostCombatStates.TryRemove(stateKey, out var suspendedState) &&
                    suspendedState.State.TargetPlayerId == nearestTarget.PlayerId &&
                    suspendedState.State.WeaponItemId == attacker.WeaponItemId &&
                    nowUtc >= suspendedState.LostAtUtc &&
                    nowUtc - suspendedState.LostAtUtc <= TargetReacquireGraceDuration)
                {
                    var suspensionDuration = nowUtc - suspendedState.LostAtUtc;
                    nextCombatState = suspendedState.State with
                    {
                        TargetWeaponItemId = nearestTarget.WeaponItemId,
                        Area = attacker.Area,
                        AimReadyAtUtc = suspendedState.State.AimReadyAtUtc.Add(suspensionDuration),
                        NextAttackAtUtc = suspendedState.State.NextAttackAtUtc.Add(suspensionDuration)
                    };
                }
                else
                {
                    _recentlyLostCombatStates.TryRemove(stateKey, out _);

                    var aimReadyAtUtc = nowUtc.Add(AimDuration).AddSeconds(
                        Math.Max(0f, attacker.InitialAttackDelaySeconds));
                    int initialBurstAttackCount = 0;
                    if (attacker.InitialBurstAttackCount > 0)
                    {
                        bool burstCharged = !_burstRechargeReadyAtUtc.TryGetValue(
                                                stateKey, out var burstReadyAtUtc) ||
                                            nowUtc >= burstReadyAtUtc;
                        if (burstCharged)
                            initialBurstAttackCount = attacker.InitialBurstAttackCount;
                        _burstRechargeReadyAtUtc[stateKey] = DateTime.MaxValue;
                    }

                    nextCombatState = new CombatState(
                        nearestTarget.PlayerId,
                        attacker.WeaponItemId,
                        nearestTarget.WeaponItemId,
                        attacker.Area,
                        aimReadyAtUtc,
                        aimReadyAtUtc,
                        initialBurstAttackCount);
                }

                _combatStates[stateKey] = nextCombatState;
                onTargetAcquired?.Invoke(new ProximityCombatTargetEvent(
                    attacker.PlayerId,
                    nearestTarget.PlayerId,
                    attacker.Area,
                    attacker.WeaponItemId,
                    nearestTarget.WeaponItemId,
                    AsUtcOffset(nowUtc),
                    ""));
                continue;
            }

            if (nowUtc < combatState.AimReadyAtUtc || nowUtc < combatState.NextAttackAtUtc)
                continue;

            int targetCount = Math.Min(Math.Max(1, attacker.MaxTargets), eligibleTargets.Count);
            for (int i = 0; i < targetCount; i++)
            {
                int damage = i == 0
                    ? attacker.Damage
                    : (int)Math.Ceiling(attacker.Damage * attacker.AdditionalTargetDamageMultiplier);
                attacks.Add(new ProximityCombatAttack(
                    attacker.PlayerId,
                    eligibleTargets[i].Actor.PlayerId,
                    attacker.Area,
                    attacker.WeaponItemId,
                    damage,
                    attacker.ProjectileWidth,
                    attacker.EffectDurationSeconds,
                    eligibleTargets.Count,
                    attacker.SunResonanceStage,
                    attacker.WaveResonanceArmed,
                    AttackerItemUid: attacker.WeaponItemUid,
                    Origin: attacker.Position,
                    AnchorPosition: eligibleTargets[i].Actor.Position,
                    AttackerTrailOrdinal: attacker.TrailOrdinal));
            }

            // A burst of N attacks has N - 1 shortened gaps between those attacks.
            // The interval after the final burst attack returns to the base cadence.
            bool useInitialBurst = attacker.InitialBurstAttackCount > 0 &&
                                   combatState.RemainingInitialBurstAttacks > 1;
            float nextAttackIntervalSeconds = useInitialBurst
                ? attacker.AttackIntervalSeconds * attacker.InitialBurstAttackIntervalMultiplier
                : attacker.AttackIntervalSeconds;
            _combatStates[stateKey] = combatState with
            {
                NextAttackAtUtc = nowUtc.AddSeconds(nextAttackIntervalSeconds),
                RemainingInitialBurstAttacks = useInitialBurst
                    ? combatState.RemainingInitialBurstAttacks - 1
                    : 0
            };
        }

        foreach (var key in _combatStates.Keys)
        {
            if (key.MatchingId != matchingId ||
                activeAttackers.Contains((key.PlayerId, key.ItemUid, key.StackIndex)))
                continue;
            if (_combatStates.TryRemove(key, out var previousState))
            {
                // 유예 보존 (#226 케이던스 수리): 문 통과·구역 깜빡임으로 한두 틱 액터에서
                // 빠졌다 돌아오면 조준을 이어간다 — 즉시 삭제는 태양의 발사 지연을 매번
                // 처음부터 다시 지불하게 해 이동 조우에서 첫 발이 영영 안 나갔다.
                _recentlyLostCombatStates[key] = new SuspendedCombatState(previousState, nowUtc);
                onTargetLost?.Invoke(CreateTargetEvent(
                    key.PlayerId, previousState, nowUtc, "attacker_inactive"));
            }
            _burstRechargeReadyAtUtc.TryRemove(key, out _);
        }

        foreach (var key in _recentlyLostCombatStates.Keys)
        {
            if (key.MatchingId != matchingId)
                continue;

            // 시간 기준 정리 (#226 케이던스 수리): 비활성 즉시 삭제는 위의 유예 보존을
            // 무효화한다 — 재획득 유예(1.5초)를 넘긴 것만 지운다.
            if (_recentlyLostCombatStates.TryGetValue(key, out var suspended) &&
                nowUtc - suspended.LostAtUtc > TargetReacquireGraceDuration)
                _recentlyLostCombatStates.TryRemove(key, out _);
        }

        return attacks;
    }

    private static int CompareTargetPriority(
        (ProximityCombatActor Actor, float DistanceSquared) left,
        (ProximityCombatActor Actor, float DistanceSquared) right,
        long currentTargetPlayerId)
    {
        int priorityComparison = GetTargetPriority(left.Actor, currentTargetPlayerId)
            .CompareTo(GetTargetPriority(right.Actor, currentTargetPlayerId));
        if (priorityComparison != 0)
            return priorityComparison;

        // SB 타겟 고정 (#219): 같은 우선순위면 거리보다 현재 타겟 유지가 먼저다 —
        // 더 가까운 후보가 나타나도 안 바꾸고, 타겟이 사거리를 벗어나야 재탐색한다.
        if (left.Actor.PlayerId == currentTargetPlayerId) return -1;
        if (right.Actor.PlayerId == currentTargetPlayerId) return 1;

        int distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return distanceComparison != 0
            ? distanceComparison
            : left.Actor.PlayerId.CompareTo(right.Actor.PlayerId);
    }

    private static int GetTargetPriority(ProximityCombatActor target, long currentTargetPlayerId)
    {
        if (target.TargetPriority >= 0)
            return target.TargetPriority;
        if (!target.IsMonsterTarget)
            return 0;
        if (target.PlayerId == currentTargetPlayerId)
            return target.IsCoreMonsterTarget ? 1 : 2;
        return 3;
    }

    public void RemoveMatching(long matchingId)
    {
        foreach (var key in _combatStates.Keys)
        {
            if (key.MatchingId != matchingId)
                continue;

            _combatStates.TryRemove(key, out _);
            _burstRechargeReadyAtUtc.TryRemove(key, out _);
        }

        foreach (var key in _recentlyLostCombatStates.Keys)
        {
            if (key.MatchingId != matchingId)
                continue;

            _recentlyLostCombatStates.TryRemove(key, out _);
        }
    }

    public void Clear()
    {
        _combatStates.Clear();
        _burstRechargeReadyAtUtc.Clear();
        _recentlyLostCombatStates.Clear();
    }

    private static ProximityCombatTargetEvent CreateTargetEvent(
        long attackerPlayerId,
        CombatState state,
        DateTime nowUtc,
        string reason)
    {
        return new ProximityCombatTargetEvent(
            attackerPlayerId,
            state.TargetPlayerId,
            state.Area,
            state.WeaponItemId,
            state.TargetWeaponItemId,
            AsUtcOffset(nowUtc),
            reason);
    }

    private static DateTimeOffset AsUtcOffset(DateTime value)
    {
        return new DateTimeOffset(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
    }

    private static int CompareWeaponInstance(ProximityCombatActor left, ProximityCombatActor right)
    {
        int uidComparison = left.WeaponItemUid.CompareTo(right.WeaponItemUid);
        return uidComparison != 0
            ? uidComparison
            : left.WeaponStackIndex.CompareTo(right.WeaponStackIndex);
    }

    /// <summary>
    ///     발사 환불 (#232 교차사격 예고 상한): 이번 틱에 뽑힌 공격을 호출부가 실행하지 못했을 때
    ///     (같은 틱에 여러 오브가 함께 준비돼 예고 상한을 넘김) 그 오브의 다음 발사 시각을 지금으로
    ///     되돌린다 — 쿨다운을 소모하지 않고 다음 틱에 다시 시도한다(필터가 자리를 열어 줄 때까지).
    ///     조준·표적은 유지한다. 같은 무기 uid의 모든 스택에 적용한다.
    /// </summary>
    public void RefundAttack(long matchingId, long playerId, long itemUid, DateTime nowUtc)
    {
        foreach (var key in _combatStates.Keys)
        {
            if (key.MatchingId != matchingId || key.PlayerId != playerId || key.ItemUid != itemUid)
                continue;
            if (_combatStates.TryGetValue(key, out var state))
                _combatStates[key] = state with { NextAttackAtUtc = nowUtc };
        }
    }

    private readonly record struct CombatState(
        long TargetPlayerId,
        int WeaponItemId,
        int TargetWeaponItemId,
        AreaType Area,
        DateTime AimReadyAtUtc,
        DateTime NextAttackAtUtc,
        int RemainingInitialBurstAttacks);

    private readonly record struct SuspendedCombatState(
        CombatState State,
        DateTime LostAtUtc);
}
