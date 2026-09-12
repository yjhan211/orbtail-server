using game_server.matches.combat;
using game_server.players;
using network.common;

namespace game_server.matches;

/// <summary>
///     자동공격 판단. 틱마다 액터 목록에서 무기별 표적을 고르고 조준·발사 주기를 플레이어의 자동공격 상태에 갱신한 뒤,
///     이번 틱에 실제로 쏠 공격 목록을 돌려준다. 피해 적용은 MatchCombatService가 맡는다. 매치 잠금 안에서 부른다.
/// </summary>
internal sealed class MatchAutoAttackService
{
    public static readonly TimeSpan AimDuration = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan TargetReacquireGraceDuration = TimeSpan.FromSeconds(1.5);

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
        var activeWeapons = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();
        foreach (var attacker in actors)
        {
            var owner = runtime.GetParticipant(attacker.PlayerId);
            if (owner == null)
            {
                continue;
            }
            var state = owner.AutoAttack;
            var weaponKey = (attacker.WeaponItemUid, attacker.WeaponStackIndex);
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None || attacker.AttackRange <= 0f || attacker.Damage <= 0 || attacker.AttackIntervalSeconds <= 0f)
            {
                state.Engagements.Remove(weaponKey);
                state.BurstRechargeReadyAtUtc.Remove(weaponKey);
                continue;
            }

            if (attacker.InitialBurstAttackCount <= 0)
            {
                state.BurstRechargeReadyAtUtc.Remove(weaponKey);
            }

            activeWeapons.Add((attacker.PlayerId, weaponKey.WeaponItemUid, weaponKey.WeaponStackIndex));
            bool hasEngagement = state.Engagements.TryGetValue(weaponKey, out var engagement);
            if (hasEngagement && engagement.Phase == AutoAttackPhase.Suspended && nowUtc - engagement.LostAtUtc > TargetReacquireGraceDuration)
            {
                state.Engagements.Remove(weaponKey);
                hasEngagement = false;
            }
            bool isEngaged = hasEngagement && engagement.Phase == AutoAttackPhase.Engaged;
            bool isSuspended = hasEngagement && engagement.Phase == AutoAttackPhase.Suspended;

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
                if (isEngaged)
                {
                    state.Engagements[weaponKey] = engagement with { Phase = AutoAttackPhase.Suspended, LostAtUtc = nowUtc };
                    if (attacker.InitialBurstAttackCount > 0 && attacker.BurstRechargeSeconds > 0f)
                    {
                        state.BurstRechargeReadyAtUtc[weaponKey] = nowUtc.AddSeconds(attacker.BurstRechargeSeconds);
                    }
                }
                continue;
            }

            eligibleTargets.Sort((left, right) => CompareTargets(left, right, isEngaged ? engagement.TargetPlayerId : 0));
            var selectedTarget = eligibleTargets[0].Actor;
            if (!isEngaged || engagement.TargetPlayerId != selectedTarget.PlayerId || engagement.WeaponItemId != attacker.WeaponItemId)
            {
                AutoAttackEngagement nextEngagement;
                bool resumesSuspended = isSuspended && engagement.TargetPlayerId == selectedTarget.PlayerId && engagement.WeaponItemId == attacker.WeaponItemId && nowUtc >= engagement.LostAtUtc;
                if (resumesSuspended)
                {
                    var suspensionDuration = nowUtc - engagement.LostAtUtc;
                    nextEngagement = engagement with
                    {
                        Phase = AutoAttackPhase.Engaged,
                        AimReadyAtUtc = engagement.AimReadyAtUtc.Add(suspensionDuration),
                        NextAttackAtUtc = engagement.NextAttackAtUtc.Add(suspensionDuration),
                        LostAtUtc = default
                    };
                }
                else
                {
                    var aimReadyAtUtc = nowUtc.Add(AimDuration).AddSeconds(Math.Max(0f, attacker.InitialAttackDelaySeconds));
                    int initialBurstAttackCount = 0;
                    if (attacker.InitialBurstAttackCount > 0)
                    {
                        bool burstCharged = !state.BurstRechargeReadyAtUtc.TryGetValue(weaponKey, out var burstReadyAtUtc) || nowUtc >= burstReadyAtUtc;
                        if (burstCharged)
                        {
                            initialBurstAttackCount = attacker.InitialBurstAttackCount;
                        }
                        state.BurstRechargeReadyAtUtc[weaponKey] = DateTime.MaxValue;
                    }
                    var nextAttackAtUtc = hasEngagement && engagement.NextAttackAtUtc > aimReadyAtUtc ? engagement.NextAttackAtUtc : aimReadyAtUtc;
                    nextEngagement = new AutoAttackEngagement(AutoAttackPhase.Engaged, selectedTarget.PlayerId, attacker.WeaponItemId, aimReadyAtUtc, nextAttackAtUtc, initialBurstAttackCount);
                }

                state.Engagements[weaponKey] = nextEngagement;
                continue;
            }

            if (nowUtc < engagement.AimReadyAtUtc || nowUtc < engagement.NextAttackAtUtc)
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
                    eligibleTargets.Count,
                    AttackerItemUid: attacker.WeaponItemUid,
                    Origin: attacker.Position,
                    AnchorPosition: eligibleTargets[i].Actor.Position,
                    AttackerTrailOrdinal: attacker.TrailOrdinal));
            }

            bool useInitialBurst = attacker.InitialBurstAttackCount > 0 && engagement.RemainingInitialBurstAttacks > 1;
            float nextAttackIntervalSeconds = useInitialBurst ? attacker.AttackIntervalSeconds * attacker.InitialBurstAttackIntervalMultiplier : attacker.AttackIntervalSeconds;
            state.Engagements[weaponKey] = engagement with
            {
                NextAttackAtUtc = nowUtc.AddSeconds(nextAttackIntervalSeconds),
                RemainingInitialBurstAttacks = useInitialBurst ? engagement.RemainingInitialBurstAttacks - 1 : 0
            };
        }

        foreach (var player in runtime.GetPlayers())
        {
            var state = player.AutoAttack;
            foreach (var weaponKey in state.Engagements.Keys.ToArray())
            {
                var engagement = state.Engagements[weaponKey];
                bool active = activeWeapons.Contains((player.PlayerId, weaponKey.ItemUid, weaponKey.StackIndex));
                if (!active && engagement.Phase == AutoAttackPhase.Engaged)
                {
                    state.Engagements[weaponKey] = engagement with { Phase = AutoAttackPhase.Suspended, LostAtUtc = nowUtc };
                    state.BurstRechargeReadyAtUtc.Remove(weaponKey);
                    continue;
                }
                if (!active)
                {
                    state.BurstRechargeReadyAtUtc.Remove(weaponKey);
                }
                if (engagement.Phase == AutoAttackPhase.Suspended && nowUtc - engagement.LostAtUtc > TargetReacquireGraceDuration)
                {
                    state.Engagements.Remove(weaponKey);
                }
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

    private static int CompareWeaponInstance(ProximityCombatActor left, ProximityCombatActor right)
    {
        int uidComparison = left.WeaponItemUid.CompareTo(right.WeaponItemUid);
        return uidComparison != 0 ? uidComparison : left.WeaponStackIndex.CompareTo(right.WeaponStackIndex);
    }
}
