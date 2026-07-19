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
    Cell? Cell = null);

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
///     Selects one nearest target per armed actor while keeping attack cadence server-authoritative.
///     Damage application stays outside this class so every volley is selected from one shared snapshot.
/// </summary>
public sealed class ProximityAutoCombatResolver
{
    public static readonly TimeSpan AimDuration = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId), CombatState> _combatStates = new();

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
                continue;
            }

            activeAttackers.Add(attacker.PlayerId);
            float attackRangeSquared = attacker.AttackRange * attacker.AttackRange;
            ProximityCombatActor? nearestTarget = null;
            float nearestDistanceSquared = float.MaxValue;

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

                if (distanceSquared < nearestDistanceSquared ||
                    Math.Abs(distanceSquared - nearestDistanceSquared) < 0.0001f &&
                    (!nearestTarget.HasValue || candidate.PlayerId < nearestTarget.Value.PlayerId))
                {
                    nearestTarget = candidate;
                    nearestDistanceSquared = distanceSquared;
                }
            }

            if (!nearestTarget.HasValue)
            {
                if (_combatStates.TryRemove(stateKey, out var previousState))
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId, previousState, nowUtc, "out_of_range_or_los"));
                continue;
            }

            bool hasCombatState = _combatStates.TryGetValue(stateKey, out var combatState);
            if (!hasCombatState ||
                combatState.TargetPlayerId != nearestTarget.Value.PlayerId ||
                combatState.WeaponItemId != attacker.WeaponItemId)
            {
                if (hasCombatState)
                {
                    string reason = combatState.TargetPlayerId != nearestTarget.Value.PlayerId
                        ? "target_changed"
                        : "weapon_changed";
                    onTargetLost?.Invoke(CreateTargetEvent(
                        attacker.PlayerId,
                        combatState,
                        nowUtc,
                        reason));
                }

                var aimReadyAtUtc = nowUtc.Add(AimDuration);
                _combatStates[stateKey] = new CombatState(
                    nearestTarget.Value.PlayerId,
                    attacker.WeaponItemId,
                    nearestTarget.Value.WeaponItemId,
                    attacker.Area,
                    aimReadyAtUtc,
                    aimReadyAtUtc);
                onTargetAcquired?.Invoke(new ProximityCombatTargetEvent(
                    attacker.PlayerId,
                    nearestTarget.Value.PlayerId,
                    attacker.Area,
                    attacker.WeaponItemId,
                    nearestTarget.Value.WeaponItemId,
                    AsUtcOffset(nowUtc),
                    ""));
                continue;
            }

            if (nowUtc < combatState.AimReadyAtUtc || nowUtc < combatState.NextAttackAtUtc)
                continue;

            attacks.Add(new ProximityCombatAttack(
                attacker.PlayerId,
                nearestTarget.Value.PlayerId,
                attacker.Area,
                attacker.WeaponItemId,
                attacker.Damage,
                attacker.ProjectileWidth,
                attacker.EffectDurationSeconds));
            _combatStates[stateKey] = combatState with
            {
                NextAttackAtUtc = nowUtc.AddSeconds(attacker.AttackIntervalSeconds)
            };
        }

        foreach (var key in _combatStates.Keys)
        {
            if (key.MatchingId != matchingId || activeAttackers.Contains(key.PlayerId))
                continue;
            if (_combatStates.TryRemove(key, out var previousState))
                onTargetLost?.Invoke(CreateTargetEvent(
                    key.PlayerId, previousState, nowUtc, "attacker_inactive"));
        }

        return attacks;
    }

    public void RemoveMatching(long matchingId)
    {
        foreach (var key in _combatStates.Keys)
        {
            if (key.MatchingId == matchingId)
                _combatStates.TryRemove(key, out _);
        }
    }

    public void Clear() => _combatStates.Clear();

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
        DateTime NextAttackAtUtc);
}
