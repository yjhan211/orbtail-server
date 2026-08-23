using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public readonly record struct DodgeableProjectileLaunch(
    long ProjectileId,
    long MatchingId,
    ProximityCombatAttack Attack,
    MapId MapId,
    Cell OriginCell,
    Vector3f Origin,
    Vector3f AimPosition,
    float MaxRange,
    DateTime LaunchedAtUtc,
    DateTime ImpactAtUtc);

public readonly record struct DodgeableProjectileResolution(
    DodgeableProjectileLaunch Launch,
    string Outcome,
    IReadOnlyList<ProximityCombatAttack> Hits,
    float TargetDisplacement,
    float HitRadius);

/// <summary>
///     Resolves server-authoritative PvP projectile impacts. Hope follows its target while
///     Despair remains fixed at the launch-time target area; Wind resolves immediately elsewhere.
/// </summary>
public sealed class DodgeableProjectileResolver
{
    private readonly Dictionary<long, List<DodgeableProjectileLaunch>> _pendingByMatching = new();
    private long _nextProjectileId;

    public IReadOnlyList<DodgeableProjectileLaunch> Queue(
        long matchingId,
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<ProximityCombatActor> playerActors,
        DateTime nowUtc)
    {
        if (matchingId <= 0 || attacks.Count == 0)
            return [];

        var spatialActors = playerActors
            .Where(actor => !actor.IsMonsterTarget)
            .GroupBy(actor => actor.PlayerId)
            .ToDictionary(group => group.Key, group => group.First());
        var launches = new List<DodgeableProjectileLaunch>(attacks.Count);

        foreach (var attack in attacks)
        {
            OrbAttackPattern attackPattern = OrbData.GetAttackPattern(attack.WeaponItemId);
            if (attack.IsResonanceProc ||
                attackPattern is not (OrbAttackPattern.HomingProjectile or
                    OrbAttackPattern.TargetArea) ||
                !spatialActors.TryGetValue(attack.AttackerPlayerId, out var attacker) ||
                !spatialActors.TryGetValue(attack.TargetPlayerId, out var target) ||
                attacker.Area != attack.Area || target.Area != attack.Area ||
                attacker.MapId == MapId.None || attacker.MapId != target.MapId ||
                attacker.Cell is null)
            {
                continue;
            }

            float distance = Distance(attacker.Position, target.Position);
            float delaySeconds = OrbData.GetPvpProjectileImpactDelaySeconds(
                attack.WeaponItemId,
                distance);
            launches.Add(new DodgeableProjectileLaunch(
                Interlocked.Increment(ref _nextProjectileId),
                matchingId,
                attack,
                attacker.MapId,
                attacker.Cell,
                attacker.Position,
                target.Position,
                attacker.AttackRange,
                nowUtc,
                nowUtc.AddSeconds(delaySeconds)));
        }

        if (launches.Count == 0)
            return launches;

        lock (_pendingByMatching)
        {
            if (!_pendingByMatching.TryGetValue(matchingId, out var pending))
            {
                pending = new List<DodgeableProjectileLaunch>();
                _pendingByMatching[matchingId] = pending;
            }

            pending.AddRange(launches);
        }

        return launches;
    }

    public IReadOnlyList<DodgeableProjectileResolution> ResolveImpacts(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> combatReadyPlayerActors,
        DateTime nowUtc,
        IReadOnlyCollection<ProximityCombatActor>? observedPlayerActors = null)
    {
        List<DodgeableProjectileLaunch> due;
        lock (_pendingByMatching)
        {
            if (!_pendingByMatching.TryGetValue(matchingId, out var pending) || pending.Count == 0)
                return [];

            due = pending.Where(projectile => projectile.ImpactAtUtc <= nowUtc).ToList();
            if (due.Count == 0)
                return [];

            pending.RemoveAll(projectile => projectile.ImpactAtUtc <= nowUtc);
            if (pending.Count == 0)
                _pendingByMatching.Remove(matchingId);
        }

        var targets = combatReadyPlayerActors
            .Where(actor => !actor.IsMonsterTarget)
            .GroupBy(actor => actor.PlayerId)
            .Select(group => group.First())
            .ToArray();
        var observedTargets = (observedPlayerActors ?? combatReadyPlayerActors)
            .Where(actor => !actor.IsMonsterTarget)
            .GroupBy(actor => actor.PlayerId)
            .ToDictionary(group => group.Key, group => group.First());
        var combatReadyTargetIds = targets.Select(target => target.PlayerId).ToHashSet();
        var resolved = new List<DodgeableProjectileResolution>(due.Count);

        foreach (var projectile in due)
        {
            if (!TryValidateAttacker(projectile, observedTargets, combatReadyTargetIds, out string attackerOutcome))
            {
                resolved.Add(new DodgeableProjectileResolution(projectile, attackerOutcome, [], 0f, 0f));
                continue;
            }

            OrbAttackPattern attackPattern = OrbData.GetAttackPattern(
                projectile.Attack.WeaponItemId);
            float hitRadius;
            IReadOnlyList<ProximityCombatAttack> hits;
            if (attackPattern == OrbAttackPattern.HomingProjectile)
            {
                hitRadius = 0f;
                var primaryTarget = targets.FirstOrDefault(target =>
                    target.PlayerId == projectile.Attack.TargetPlayerId);
                hits = primaryTarget.PlayerId != 0 && IsValidHomingTarget(projectile, primaryTarget)
                    ? [projectile.Attack]
                    : [];
            }
            else
            {
                // 직선탄 (#226 재정의): 발사 시점 조준 위치 고정 — 단일 대상, 반경을 벗어나
                // 이동했으면 dodged. (구 TargetArea = 파도 광역 스플래시는 물폭탄 전환으로 퇴역.)
                hitRadius = OrbData.GetPvpProjectileHitRadius(
                    projectile.Attack.WeaponItemId,
                    projectile.Attack.ProjectileWidth);
                var primaryTarget = targets.FirstOrDefault(target =>
                    target.PlayerId == projectile.Attack.TargetPlayerId);
                hits = primaryTarget.PlayerId != 0 &&
                       IsValidTarget(projectile, primaryTarget) &&
                       IsWithinRadius(primaryTarget.Position, projectile.AimPosition, hitRadius)
                    ? [projectile.Attack]
                    : [];
            }

            observedTargets.TryGetValue(projectile.Attack.TargetPlayerId, out var observedTarget);
            float displacement = observedTarget.PlayerId == 0
                ? 0f
                : Distance(observedTarget.Position, projectile.AimPosition);
            string outcome = hits.Count > 0
                ? "hit"
                : ClassifyTargetMiss(
                    projectile,
                    observedTarget,
                    combatReadyTargetIds,
                    attackPattern,
                    displacement,
                    hitRadius);
            resolved.Add(new DodgeableProjectileResolution(projectile, outcome, hits, displacement, hitRadius));
        }

        return resolved;
    }

    private static bool TryValidateAttacker(
        DodgeableProjectileLaunch projectile,
        IReadOnlyDictionary<long, ProximityCombatActor> observedActors,
        IReadOnlySet<long> combatReadyPlayerIds,
        out string outcome)
    {
        if (!observedActors.TryGetValue(projectile.Attack.AttackerPlayerId, out var attacker))
        {
            outcome = "attacker_unavailable";
            return false;
        }
        if (attacker.Area != projectile.Attack.Area || attacker.MapId != projectile.MapId)
        {
            outcome = "attacker_left_area";
            return false;
        }
        if (!combatReadyPlayerIds.Contains(attacker.PlayerId))
        {
            outcome = "attacker_not_combat_ready";
            return false;
        }

        outcome = "hit";
        return true;
    }

    private static string ClassifyTargetMiss(
        DodgeableProjectileLaunch projectile,
        ProximityCombatActor target,
        IReadOnlySet<long> combatReadyPlayerIds,
        OrbAttackPattern attackPattern,
        float displacement,
        float hitRadius)
    {
        if (target.PlayerId == 0)
            return "target_unavailable";
        if (target.Area != projectile.Attack.Area)
            return "target_left_area";
        if (target.MapId != projectile.MapId)
            return "target_changed_map";
        if (target.Cell is null)
            return "target_cell_unavailable";
        if (!combatReadyPlayerIds.Contains(target.PlayerId))
            return "target_not_combat_ready";
        if (!ProximityCombatLineOfSight.HasClearPath(projectile.MapId, projectile.OriginCell, target.Cell))
            return "line_of_sight_blocked";
        if (attackPattern == OrbAttackPattern.HomingProjectile &&
            Distance(projectile.Origin, target.Position) > projectile.MaxRange)
        {
            return "target_out_of_range";
        }
        if (attackPattern != OrbAttackPattern.HomingProjectile && displacement > hitRadius)
            return "dodged";

        return "unresolved_miss";
    }

    public void RemoveMatching(long matchingId)
    {
        lock (_pendingByMatching)
            _pendingByMatching.Remove(matchingId);
    }

    public void Clear()
    {
        lock (_pendingByMatching)
            _pendingByMatching.Clear();
    }

    private static bool IsValidHomingTarget(
        DodgeableProjectileLaunch projectile,
        ProximityCombatActor target)
    {
        return IsValidTarget(projectile, target) &&
               Distance(projectile.Origin, target.Position) <= projectile.MaxRange;
    }

    private static bool IsValidTarget(DodgeableProjectileLaunch projectile, ProximityCombatActor target)
    {
        if (target.PlayerId == projectile.Attack.AttackerPlayerId ||
            target.Area != projectile.Attack.Area ||
            target.MapId != projectile.MapId ||
            target.Cell is null)
        {
            return false;
        }

        return ProximityCombatLineOfSight.HasClearPath(
            projectile.MapId,
            projectile.OriginCell,
            target.Cell);
    }

    private static bool IsWithinRadius(Vector3f position, Vector3f center, float radius)
    {
        float dx = position.X - center.X;
        float dy = position.Y - center.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static float Distance(Vector3f left, Vector3f right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
