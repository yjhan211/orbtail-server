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
    bool OrbEffectActive = false);

public readonly record struct ProximityCombatAttack(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId,
    int Damage,
    float ProjectileWidth,
    float EffectDurationSeconds);

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
    public static readonly TimeSpan AimDuration = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId), CombatState> _combatStates = new();
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId), DateTime>
        _burstRechargeReadyAtUtc = new();

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
        var activeAttackers = new HashSet<long>();

        foreach (var attacker in actors)
        {
            var stateKey = (matchingId, attacker.PlayerId);
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None ||
                attacker.AttackRange <= 0f || attacker.Damage <= 0 || attacker.AttackIntervalSeconds <= 0f)
            {
                if (_combatStates.TryRemove(stateKey, out var previousState))
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId, previousState, nowUtc, "attacker_unarmed"));
                _burstRechargeReadyAtUtc.TryRemove(stateKey, out _);
                continue;
            }

            if (attacker.InitialBurstAttackCount <= 0)
                _burstRechargeReadyAtUtc.TryRemove(stateKey, out _);

            activeAttackers.Add(attacker.PlayerId);
            float attackRangeSquared = attacker.AttackRange * attacker.AttackRange;
            var eligibleTargets = new List<(ProximityCombatActor Actor, float DistanceSquared)>();

            foreach (var candidate in actors)
            {
                if (candidate.PlayerId == attacker.PlayerId || candidate.Area != attacker.Area)
                    continue;

                float dx = attacker.Position.X - candidate.Position.X;
                float dy = attacker.Position.Y - candidate.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > attackRangeSquared)
                    continue;
                if (hasLineOfSight != null && !hasLineOfSight(attacker, candidate))
                    continue;

                eligibleTargets.Add((candidate, distanceSquared));
            }

            if (eligibleTargets.Count == 0)
            {
                if (_combatStates.TryRemove(stateKey, out var previousState))
                {
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId, previousState, nowUtc, "out_of_range_or_los"));
                    if (attacker.InitialBurstAttackCount > 0 && attacker.BurstRechargeSeconds > 0f)
                        _burstRechargeReadyAtUtc[stateKey] =
                            nowUtc.AddSeconds(attacker.BurstRechargeSeconds);
                }
                continue;
            }

            eligibleTargets.Sort(static (left, right) =>
            {
                int distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
                return distanceComparison != 0
                    ? distanceComparison
                    : left.Actor.PlayerId.CompareTo(right.Actor.PlayerId);
            });
            var nearestTarget = eligibleTargets[0].Actor;

            bool hasCombatState = _combatStates.TryGetValue(stateKey, out var combatState);
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
                }

                var aimReadyAtUtc = nowUtc.Add(AimDuration);
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

                _combatStates[stateKey] = new CombatState(
                    nearestTarget.PlayerId,
                    attacker.WeaponItemId,
                    nearestTarget.WeaponItemId,
                    attacker.Area,
                    aimReadyAtUtc,
                    aimReadyAtUtc,
                    initialBurstAttackCount);
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
                    attacker.EffectDurationSeconds));
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
            if (key.MatchingId != matchingId || activeAttackers.Contains(key.PlayerId))
                continue;
            if (_combatStates.TryRemove(key, out var previousState))
            {
                onTargetLost?.Invoke(CreateTargetEvent(
                    key.PlayerId, previousState, nowUtc, "attacker_inactive"));
            }
            _burstRechargeReadyAtUtc.TryRemove(key, out _);
        }

        return attacks;
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
    }

    public void Clear()
    {
        _combatStates.Clear();
        _burstRechargeReadyAtUtc.Clear();
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

    private readonly record struct CombatState(
        long TargetPlayerId,
        int WeaponItemId,
        int TargetWeaponItemId,
        AreaType Area,
        DateTime AimReadyAtUtc,
        DateTime NextAttackAtUtc,
        int RemainingInitialBurstAttacks);
}
