using network.common;

namespace game_server.matches.combat;

/// <summary>
///     매치 하나의 자동공격 해석 상태. 무기 단위로 현재 표적·조준 완료·다음 발사 시각을 기억하고, 틱마다 실제로 쏠 공격을 고른다.
///     MatchRuntime이 소유하며 매치 잠금 안에서만 접근한다 — 세 사전은 함께 바뀌므로 잠금 밖 접근은 없다.
/// </summary>
public sealed class MatchAutoAttackState
{
    private bool _released;
    public static readonly TimeSpan AimDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TargetReacquireGraceDuration = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<(long PlayerId, long ItemUid, int StackIndex), CombatState> _combatStates = new();
    private readonly Dictionary<(long PlayerId, long ItemUid, int StackIndex), DateTime> _burstRechargeReadyAtUtc = new();
    private readonly Dictionary<(long PlayerId, long ItemUid, int StackIndex), SuspendedCombatState> _recentlyLostCombatStates = new();

    public IReadOnlyList<ProximityCombatAttack> UpdateAttacks(IReadOnlyList<ProximityCombatActor> actors, DateTime nowUtc, Func<ProximityCombatActor, ProximityCombatActor, bool>? hasLineOfSight = null)
    {
        if (_released)
        {
            return [];
        }

        var attacks = new List<ProximityCombatAttack>();
        var activeAttackers = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();
        foreach (var attacker in actors)
        {
            var attackerKey = (attacker.PlayerId, attacker.WeaponItemUid, attacker.WeaponStackIndex);
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None || attacker.AttackRange <= 0f || attacker.Damage <= 0 || attacker.AttackIntervalSeconds <= 0f)
            {
                _combatStates.Remove(attackerKey);
                _burstRechargeReadyAtUtc.Remove(attackerKey);
                _recentlyLostCombatStates.Remove(attackerKey);
                continue;
            }

            if (attacker.InitialBurstAttackCount <= 0)
            {
                _burstRechargeReadyAtUtc.Remove(attackerKey);
            }

            activeAttackers.Add(attackerKey);
            if (_recentlyLostCombatStates.TryGetValue(attackerKey, out var expiredState) && nowUtc - expiredState.LostAtUtc > TargetReacquireGraceDuration)
            {
                _recentlyLostCombatStates.Remove(attackerKey);
            }
            float attackRangeSquared = attacker.AttackRange * attacker.AttackRange;
            var eligibleTargetsByPlayer = new Dictionary<long, (ProximityCombatActor Actor, float DistanceSquared)>();
            foreach (var candidate in actors)
            {
                if (candidate.Untargetable || candidate.PlayerId == attacker.PlayerId || candidate.Area != attacker.Area)
                {
                    continue;
                }

                float dx = attacker.Position.X - candidate.Position.X;
                float dy = attacker.Position.Y - candidate.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > attackRangeSquared)
                {
                    continue;
                }

                if (hasLineOfSight != null && !hasLineOfSight(attacker, candidate))
                {
                    continue;
                }

                if (!eligibleTargetsByPlayer.TryGetValue(candidate.PlayerId, out var existing) || distanceSquared < existing.DistanceSquared || distanceSquared.Equals(existing.DistanceSquared) && CompareWeaponInstance(candidate, existing.Actor) < 0)
                {
                    eligibleTargetsByPlayer[candidate.PlayerId] = (candidate, distanceSquared);
                }
            }

            var eligibleTargets = eligibleTargetsByPlayer.Values.ToList();
            if (eligibleTargets.Count == 0)
            {
                if (_combatStates.Remove(attackerKey, out var previousState))
                {
                    _recentlyLostCombatStates[attackerKey] = new SuspendedCombatState(previousState, nowUtc);
                    if (attacker.InitialBurstAttackCount > 0 && attacker.BurstRechargeSeconds > 0f)
                    {
                        _burstRechargeReadyAtUtc[attackerKey] = nowUtc.AddSeconds(attacker.BurstRechargeSeconds);
                    }
                }
                continue;
            }

            bool hasCombatState = _combatStates.TryGetValue(attackerKey, out var combatState);
            eligibleTargets.Sort((left, right) => CompareTargets(left, right, hasCombatState ? combatState.TargetPlayerId : 0));
            var selectedTarget = eligibleTargets[0].Actor;
            if (!hasCombatState || combatState.TargetPlayerId != selectedTarget.PlayerId || combatState.WeaponItemId != attacker.WeaponItemId)
            {
                if (hasCombatState)
                {
                    _recentlyLostCombatStates.Remove(attackerKey);
                }

                CombatState nextCombatState;
                SuspendedCombatState suspendedState = default;
                bool hadSuspendedState = !hasCombatState && _recentlyLostCombatStates.Remove(attackerKey, out suspendedState);
                if (hadSuspendedState && suspendedState.State.TargetPlayerId == selectedTarget.PlayerId && suspendedState.State.WeaponItemId == attacker.WeaponItemId && nowUtc >= suspendedState.LostAtUtc && nowUtc - suspendedState.LostAtUtc <= TargetReacquireGraceDuration)
                {
                    var suspensionDuration = nowUtc - suspendedState.LostAtUtc;
                    nextCombatState = suspendedState.State with
                    {
                        AimReadyAtUtc = suspendedState.State.AimReadyAtUtc.Add(suspensionDuration),
                        NextAttackAtUtc = suspendedState.State.NextAttackAtUtc.Add(suspensionDuration)
                    };
                }
                else
                {
                    var aimReadyAtUtc = nowUtc.Add(AimDuration).AddSeconds(Math.Max(0f, attacker.InitialAttackDelaySeconds));
                    int initialBurstAttackCount = 0;
                    if (attacker.InitialBurstAttackCount > 0)
                    {
                        bool burstCharged = !_burstRechargeReadyAtUtc.TryGetValue(attackerKey, out var burstReadyAtUtc) || nowUtc >= burstReadyAtUtc;
                        if (burstCharged)
                        {
                            initialBurstAttackCount = attacker.InitialBurstAttackCount;
                        }
                        _burstRechargeReadyAtUtc[attackerKey] = DateTime.MaxValue;
                    }
                    var nextAttackAtUtc = hasCombatState && combatState.NextAttackAtUtc > aimReadyAtUtc ? combatState.NextAttackAtUtc : aimReadyAtUtc;
                    if (hadSuspendedState && suspendedState.State.NextAttackAtUtc > nextAttackAtUtc &&
                        nowUtc - suspendedState.LostAtUtc <= TargetReacquireGraceDuration)
                    {
                        nextAttackAtUtc = suspendedState.State.NextAttackAtUtc;
                    }
                    nextCombatState = new CombatState(selectedTarget.PlayerId, attacker.WeaponItemId, aimReadyAtUtc, nextAttackAtUtc, initialBurstAttackCount);
                }

                _combatStates[attackerKey] = nextCombatState;
                continue;
            }

            if (nowUtc < combatState.AimReadyAtUtc || nowUtc < combatState.NextAttackAtUtc)
            {
                continue;
            }

            int targetCount = Math.Min(Math.Max(1, attacker.MaxTargets), eligibleTargets.Count);
            for (int i = 0; i < targetCount; i++)
            {
                int damage = i == 0 ? attacker.Damage : (int)Math.Ceiling(attacker.Damage * attacker.AdditionalTargetDamageMultiplier);
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

            bool useInitialBurst = attacker.InitialBurstAttackCount > 0 && combatState.RemainingInitialBurstAttacks > 1;
            float nextAttackIntervalSeconds = useInitialBurst ? attacker.AttackIntervalSeconds * attacker.InitialBurstAttackIntervalMultiplier : attacker.AttackIntervalSeconds;
            _combatStates[attackerKey] = combatState with
            {
                NextAttackAtUtc = nowUtc.AddSeconds(nextAttackIntervalSeconds),
                RemainingInitialBurstAttacks = useInitialBurst ? combatState.RemainingInitialBurstAttacks - 1 : 0
            };
        }

        // 이번 틱에 공격자로 오지 않은 무기는 표적을 잃은 것으로 본다. 열거 중 지우므로 키를 먼저 복사한다.
        foreach (var key in _combatStates.Keys.ToArray())
        {
            if (activeAttackers.Contains(key))
            {
                continue;
            }
            if (_combatStates.Remove(key, out var previousState))
            {
                _recentlyLostCombatStates[key] = new SuspendedCombatState(previousState, nowUtc);
            }
            _burstRechargeReadyAtUtc.Remove(key);
        }

        foreach (var key in _recentlyLostCombatStates.Keys.ToArray())
        {
            if (nowUtc - _recentlyLostCombatStates[key].LostAtUtc > TargetReacquireGraceDuration)
            {
                _recentlyLostCombatStates.Remove(key);
            }
        }

        return attacks;
    }

    private static int CompareTargets((ProximityCombatActor Actor, float DistanceSquared) left, (ProximityCombatActor Actor, float DistanceSquared) right, long currentTargetPlayerId)
    {
        int priorityComparison = GetTargetPriority(left.Actor, currentTargetPlayerId).CompareTo(GetTargetPriority(right.Actor, currentTargetPlayerId));
        if (priorityComparison != 0)
        {
            return priorityComparison;
        }

        if (left.Actor.PlayerId == currentTargetPlayerId)
        {
            return -1;
        }

        if (right.Actor.PlayerId == currentTargetPlayerId)
        {
            return 1;
        }

        int distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return distanceComparison != 0 ? distanceComparison : left.Actor.PlayerId.CompareTo(right.Actor.PlayerId);
    }

    private static int GetTargetPriority(ProximityCombatActor target, long currentTargetPlayerId)
    {
        if (target.TargetPriority >= 0)
        {
            return target.TargetPriority;
        }

        if (!target.IsMonsterTarget)
        {
            return 0;
        }

        if (target.PlayerId == currentTargetPlayerId)
        {
            return target.IsCoreMonsterTarget ? 1 : 2;
        }
        return 3;
    }

    public void Reset()
    {
        _combatStates.Clear();
        _burstRechargeReadyAtUtc.Clear();
        _recentlyLostCombatStates.Clear();
    }

    internal void Release()
    {
        _released = true;
        Reset();
    }

    private static int CompareWeaponInstance(ProximityCombatActor left, ProximityCombatActor right)
    {
        int uidComparison = left.WeaponItemUid.CompareTo(right.WeaponItemUid);
        return uidComparison != 0 ? uidComparison : left.WeaponStackIndex.CompareTo(right.WeaponStackIndex);
    }

    public void ResetAttackCooldown(long playerId, long itemUid, DateTime nowUtc)
    {
        if (_released)
        {
            return;
        }
        foreach (var key in _combatStates.Keys.ToArray())
        {
            if (key.PlayerId != playerId || key.ItemUid != itemUid)
            {
                continue;
            }

            _combatStates[key] = _combatStates[key] with { NextAttackAtUtc = nowUtc };
        }
    }

    private readonly record struct CombatState(
        long TargetPlayerId,
        int WeaponItemId,
        DateTime AimReadyAtUtc,
        DateTime NextAttackAtUtc,
        int RemainingInitialBurstAttacks);

    private readonly record struct SuspendedCombatState(CombatState State, DateTime LostAtUtc);
}
