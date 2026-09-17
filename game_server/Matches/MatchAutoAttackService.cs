using network.common;

namespace game_server.matches;

/// <summary>
///     공격 주기가 된 오브마다 범위 안의 표적을 선택한다. 표적 유지나 조준 대기는 하지 않는다.
///     이번 틱에 발생할 공격 목록만 반환하며, 피해 적용은 MatchCombatService가 담당한다.
/// </summary>
internal sealed class MatchAutoAttackService
{
    public IReadOnlyList<ProximityCombatAttack> UpdateAttacks(MatchRuntime runtime, IReadOnlyList<ProximityCombatActor> actors, DateTime nowUtc, Func<ProximityCombatActor, ProximityCombatActor, bool>? canAttackTarget = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Auto attacks require the match lock.");
        }
        if (runtime.IsEnded)
        {
            return [];
        }

        var attacks = new List<ProximityCombatAttack>();
        foreach (var attacker in actors)
        {
            var owner = runtime.GetPlayer(attacker.PlayerId);
            if (owner == null)
            {
                continue;
            }
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None || attacker.AttackRange <= 0f || attacker.Damage <= 0 || attacker.AttackIntervalSeconds <= 0f)
            {
                continue;
            }

            if (!owner.Orbs.IsOrbAttackReady(attacker.WeaponItemUid, nowUtc))
            {
                continue;
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

                if (canAttackTarget != null && !canAttackTarget(attacker, candidate))
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
                continue;
            }

            eligibleTargets.Sort(CompareTargets);
            var selectedTarget = eligibleTargets[0].Actor;

            attacks.Add(new ProximityCombatAttack(
                attacker.PlayerId,
                selectedTarget.PlayerId,
                attacker.Area,
                attacker.WeaponItemId,
                attacker.Damage,
                eligibleTargets.Count,
                AttackerItemUid: attacker.WeaponItemUid,
                Origin: attacker.Position,
                AnchorPosition: selectedTarget.Position,
                AttackerTrailOrdinal: attacker.TrailOrdinal));
            owner.Orbs.ScheduleNextOrbAttack(attacker.WeaponItemUid, nowUtc, attacker.AttackIntervalSeconds);
        }

        return attacks;
    }

    private static int CompareTargets((ProximityCombatActor Actor, float DistanceSquared) left, (ProximityCombatActor Actor, float DistanceSquared) right)
    {
        int priorityComparison = left.Actor.TargetPriority.CompareTo(right.Actor.TargetPriority);
        if (priorityComparison != 0)
        {
            return priorityComparison;
        }
        int distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
        return distanceComparison != 0 ? distanceComparison : left.Actor.PlayerId.CompareTo(right.Actor.PlayerId);
    }

    private static int CompareWeaponInstance(ProximityCombatActor left, ProximityCombatActor right)
    {
        int uidComparison = left.WeaponItemUid.CompareTo(right.WeaponItemUid);
        return uidComparison != 0 ? uidComparison : left.WeaponStackIndex.CompareTo(right.WeaponStackIndex);
    }
}
