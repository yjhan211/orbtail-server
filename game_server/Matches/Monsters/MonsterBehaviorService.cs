using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     몬스터의 탐지·추격 대상과 담당 플레이어를 결정한다.
///     경로 계획과 이동 실행은 MatchMoveService가 담당한다.
/// </summary>
internal sealed class MonsterBehaviorService
{
    internal bool TryAcquireNearbyTarget(Monster monster, IReadOnlyList<Player> participants)
    {
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.CurrentArea != monster.Area)
            {
                continue;
            }

            float aggroDx = participant.Position!.X - monster.Position.X;
            float aggroDy = participant.Position!.Y - monster.Position.Y;
            if (aggroDx * aggroDx + aggroDy * aggroDy > Config.SWARM_MONSTER_AGGRO_RADIUS * Config.SWARM_MONSTER_AGGRO_RADIUS)
            {
                continue;
            }

            if (monster.OwnerPlayerId != 0 && monster.OwnerPlayerId != participant.PlayerId)
            {
                continue;
            }

            monster.Aggro = true;
            monster.ChaseTargetPlayerId = participant.PlayerId;
            return true;
        }

        return false;
    }

    internal bool TrySelectMovementTarget(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> participants, DateTime now, out Player target)
    {
        bool found = false;
        target = null!;
        float intruderNearestSquared = Config.SWARM_MONSTER_AGGRO_RADIUS * Config.SWARM_MONSTER_AGGRO_RADIUS;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.CurrentArea != monster.Area)
            {
                continue;
            }
            float intruderDx = participant.Position!.X - monster.Position.X;
            float intruderDy = participant.Position!.Y - monster.Position.Y;
            float intruderSquared = intruderDx * intruderDx + intruderDy * intruderDy;
            if (intruderSquared >= intruderNearestSquared)
            {
                continue;
            }
            intruderNearestSquared = intruderSquared;
            target = participant;
            found = true;
        }

        long chaseId = monster.OwnerPlayerId != 0 ? monster.OwnerPlayerId : monster.ChaseTargetPlayerId;
        for (int index = 0; index < participants.Count && !found && chaseId != 0; index++)
        {
            var participant = participants[index];
            if (participant.CurrentArea != monster.Area || participant.PlayerId != chaseId)
            {
                continue;
            }
            target = participant;
            found = true;
            break;
        }

        if (!found && monster.OwnerPlayerId != 0)
        {
            found = TryReassignOwner(runtime, monster, participants, out target);
        }

        if (!found && monster.OwnerPlayerId == 0)
        {
            float nearestSquared = float.MaxValue;
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.CurrentArea != monster.Area)
                {
                    continue;
                }
                float dx = participant.Position!.X - monster.Position.X;
                float dy = participant.Position!.Y - monster.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= nearestSquared)
                {
                    continue;
                }
                nearestSquared = distanceSquared;
                target = participant;
                found = true;
            }

            if (found)
            {
                monster.NextTargetScanAtUtc = now.AddSeconds(Config.SWARM_MONSTER_TARGET_HOLD_SECONDS);
            }
        }

        return found;
    }

    internal bool TrySelectCrossAreaTarget(Monster monster, IReadOnlyList<Player> participants, out Player target)
    {
        target = null!;
        if (monster.ChaseTargetPlayerId != 0)
        {
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.PlayerId != monster.ChaseTargetPlayerId || participant.CurrentArea == AreaType.None || participant.CurrentArea == monster.Area)
                {
                    continue;
                }

                target = participant;
                return true;
            }
        }

        return false;
    }

    private bool TryReassignOwner(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> participants, out Player target)
    {
        target = null!;
        bool found = false;
        long departedOwnerId = monster.OwnerPlayerId;
        monster.OwnerPlayerId = 0;
        long reassigned = 0;
        int leastLoad = int.MaxValue;
        bool reassignedOrbless = false;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.CurrentArea != monster.Area)
            {
                continue;
            }
            int load = 0;
            foreach (var candidate in runtime.Monsters.Entities.Values)
            {
                if (candidate.Alive && candidate.OwnerPlayerId == participant.PlayerId)
                {
                    load++;
                }
            }
            bool orbless = !runtime.GetOrbs(participant.PlayerId).HasAnyOrb();
            if (reassignedOrbless && !orbless)
            {
                continue;
            }
            if (orbless && !reassignedOrbless)
            {
                reassignedOrbless = true;
                leastLoad = load;
                reassigned = participant.PlayerId;
                continue;
            }
            if (load >= leastLoad)
            {
                continue;
            }
            leastLoad = load;
            reassigned = participant.PlayerId;
        }
        if (reassigned != 0)
        {
            monster.OwnerPlayerId = reassigned;
            for (int index = 0; index < participants.Count; index++)
            {
                if (participants[index].PlayerId != reassigned)
                {
                    continue;
                }
                target = participants[index];
                found = true;
                break;
            }
        }
        else if (monster.Aggro)
        {
            monster.ChaseTargetPlayerId = departedOwnerId;
        }
        return found;
    }

    public MovementRequest CreateMovementRequest(MatchRuntime runtime, Monster monster,
        IReadOnlyList<Player> participants, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster decisions require the match lock.");
        }
        float speed = Config.SWARM_MONSTER_MOVE_SPEED *
            (now < monster.WaveSlowUntilUtc ? OrbData.WaveSlowMoveSpeedMultiplier : 1f);
        if (runtime.Monsters.IsInitialized &&
            (now - runtime.Monsters.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
        {
            speed *= (float)Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        }
        if (runtime.IsEnded || !monster.Alive)
        {
            return new MovementRequest(null, 0f, HoldPosition: true);
        }
        if (!monster.Aggro && !TryAcquireNearbyTarget(monster, participants))
        {
            return new MovementRequest(null, speed);
        }
        bool found = TrySelectMovementTarget(runtime, monster, participants, now, out var target);
        if (!found && TrySelectCrossAreaTarget(monster, participants, out target))
        {
            var destination = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target.Position!);
            return new MovementRequest(destination, speed);
        }
        if (found)
        {
            monster.ChaseTargetPlayerId = target.PlayerId;
            bool hold = false;
            if (monster.AttackRangeValue > Monster.BaseContactRadius)
            {
                float radius = monster.AttackRangeValue * Config.SWARM_MONSTER_RANGED_HOLD_RANGE_RATIO;
                hold = GroundGeometry.IsWithinGroundRadius(monster.Position, target.Position!, radius);
            }
            return new MovementRequest(
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target.Position!), speed, hold);
        }
        // 남은 공급 경로는 유지하고, 경로가 끝나면 순찰 목표를 고른다.
        if (monster.Movement.WaypointIndex < monster.Movement.Waypoints.Count)
        {
            return new MovementRequest(null, speed);
        }
        var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
        float dx = anchor.X - monster.Position.X;
        float dy = anchor.Y - monster.Position.Y;
        float radiusFromHome = Config.SWARM_MONSTER_IDLE_PATROL_RADIUS * 2f;
        if (dx * dx + dy * dy <= radiusFromHome * radiusFromHome)
        {
            monster.ChaseTargetPlayerId = 0;
            float angle = monster.ScatterAngle +
                (float)((now - monster.SpawnedAtUtc).TotalSeconds * Config.SWARM_MONSTER_IDLE_PATROL_ANGULAR_SPEED);
            var patrol = new Vector3f(
                anchor.X + MathF.Cos(angle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS,
                anchor.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS, 0f);
            var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, patrol);
            if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell))
            {
                return new MovementRequest(cell, speed);
            }
        }
        return new MovementRequest(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, anchor), speed);
    }

}
