using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

public readonly record struct ProximityCombatActor(
    long PlayerId,
    AreaType Area,
    Vector3f Position,
    int WeaponItemId);

public readonly record struct ProximityCombatAttack(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId);

/// <summary>
///     Selects one nearest target per armed actor while keeping attack cadence server-authoritative.
///     Damage application stays outside this class so every volley is selected from one shared snapshot.
/// </summary>
public sealed class ProximityAutoCombatResolver
{
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId), DateTime> _nextAttackAtUtc = new();

    public IReadOnlyList<ProximityCombatAttack> Resolve(
        long matchingId,
        IReadOnlyList<ProximityCombatActor> actors,
        DateTime nowUtc,
        float attackRange,
        TimeSpan attackCooldown)
    {
        if (matchingId <= 0 || actors.Count < 2 || attackRange <= 0f || attackCooldown <= TimeSpan.Zero)
            return [];

        float attackRangeSquared = attackRange * attackRange;
        var attacks = new List<ProximityCombatAttack>();

        foreach (var attacker in actors)
        {
            if (attacker.WeaponItemId <= 0 || attacker.Area == AreaType.None)
                continue;

            var cooldownKey = (matchingId, attacker.PlayerId);
            if (_nextAttackAtUtc.TryGetValue(cooldownKey, out var nextAttackAtUtc) && nowUtc < nextAttackAtUtc)
                continue;

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

                if (distanceSquared < nearestDistanceSquared ||
                    Math.Abs(distanceSquared - nearestDistanceSquared) < 0.0001f &&
                    (!nearestTarget.HasValue || candidate.PlayerId < nearestTarget.Value.PlayerId))
                {
                    nearestTarget = candidate;
                    nearestDistanceSquared = distanceSquared;
                }
            }

            if (!nearestTarget.HasValue)
                continue;

            attacks.Add(new ProximityCombatAttack(
                attacker.PlayerId,
                nearestTarget.Value.PlayerId,
                attacker.Area,
                attacker.WeaponItemId));
            _nextAttackAtUtc[cooldownKey] = nowUtc.Add(attackCooldown);
        }

        return attacks;
    }

    public void RemoveMatching(long matchingId)
    {
        foreach (var key in _nextAttackAtUtc.Keys)
        {
            if (key.MatchingId == matchingId)
                _nextAttackAtUtc.TryRemove(key, out _);
        }
    }

    public void Clear() => _nextAttackAtUtc.Clear();
}
