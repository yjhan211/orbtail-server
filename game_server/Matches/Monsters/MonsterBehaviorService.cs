using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     몬스터의 침투·추격·배회 목적지와 이동 명령을 결정한다. 위치는 변경하지 않는다.
///     공통 이동 단계가 명령을 적용한 뒤 도착·행군 시간 초과를 판정한다.
/// </summary>
internal sealed class MonsterBehaviorService
{
    private const double InfiltrationGoldenAngle = 0.6180339887498949d;

    public bool TryPlanSpawnRoute(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, out Vector3f origin, out List<Vector3f> route)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster movement requires the match lock.");
        }
        route = null!;
        var originCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var state = runtime.Monsters;
        float baseAngle = ResolveInfiltrationExitBearing(runtime, destinationArea, destination, originCenter);
        double golden = (state.NextInfiltrationOriginOrdinal++ * InfiltrationGoldenAngle) % 1d;
        float angle = baseAngle + (float)((golden - 0.5d) * 2d) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_JITTER_RADIANS;
        origin = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(originCenter.X + MathF.Cos(angle) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS, originCenter.Y + MathF.Sin(angle) * Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS, 0f), originCenter, AreaType.S2Corridor9);

        float burstDistance = Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE;
        var burstPosition = new Vector3f(
            origin.X + MathF.Cos(angle) * burstDistance,
            origin.Y + MathF.Sin(angle) * burstDistance,
            0f);
        var burst = MapPathfinder.ClampToAreaWalkable(
            Config.SWARM_MATCH_MAP,
            burstPosition,
            originCenter,
            AreaType.S2Corridor9);

        bool canReachBurst = MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, origin, burst);
        if (canReachBurst)
        {
            bool routeFound = MapPathfinder.TryPlanRoute(
                Config.SWARM_MATCH_MAP,
                AreaType.S2Corridor9,
                burst,
                destinationArea,
                destination,
                candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea),
                out route);
            if (routeFound)
            {
                route.Insert(0, burst);
                return true;
            }
        }

        return MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9, origin, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out route);
    }

    private float ResolveInfiltrationExitBearing(MatchRuntime runtime, AreaType destinationArea, Vector3f destination, Vector3f originCenter)
    {
        var bearings = runtime.Monsters.InfiltrationExitBearings;
        if (bearings.TryGetValue(destinationArea, out float cached))
        {
            return cached;
        }

        float fallback = MathF.Atan2(destination.Y - originCenter.Y, destination.X - originCenter.X);
        if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9, originCenter, destinationArea, destination, candidate => IsInfiltrationRouteBlocked(runtime, candidate, destinationArea), out var probe))
        {
            return fallback;
        }

        float exitRadius = Config.SWARM_MONSTER_INFILTRATION_ORIGIN_RADIUS + Config.SWARM_MONSTER_INFILTRATION_BURST_DISTANCE;
        foreach (var point in probe)
        {
            float dx = point.X - originCenter.X;
            float dy = point.Y - originCenter.Y;
            if (dx * dx + dy * dy < exitRadius * exitRadius)
            {
                continue;
            }
            float bearing = MathF.Atan2(dy, dx);
            bearings[destinationArea] = bearing;
            return bearing;
        }

        bearings[destinationArea] = fallback;
        return fallback;
    }

    private bool IsInfiltrationRouteBlocked(MatchRuntime runtime, AreaType candidate, AreaType destinationArea)
    {
        if (runtime.Closures.IsAreaClosed(candidate))
        {
            return true;
        }

        if (candidate == destinationArea)
        {
            return false;
        }

        var rooms = MatchSpawnData.GetPhaseRoomCandidates();
        foreach (var t in rooms)
        {
            if (t == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAcquireNearbyTarget(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
            {
                continue;
            }

            float aggroDx = participant.Position.X - monster.Position.X;
            float aggroDy = participant.Position.Y - monster.Position.Y;
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

    private void PlanPathMovement(Monster monster, DateTime now, bool holdAtThreshold)
    {
        if (!holdAtThreshold || monster.Area != monster.HomeArea)
        {
            monster.Movement.FollowPath = true;
            monster.Movement.Speed = Config.SWARM_MONSTER_MOVE_SPEED * GetMonsterWaveSlowMultiplier(monster, now);
        }
    }

    internal void CompleteMovement(MatchRuntime runtime, Monster monster, bool holdAtThreshold)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Monster movement completion requires the match lock.");
        if (runtime.IsEnded || holdAtThreshold || monster.Movement.Waypoints.Count == 0)
            return;
        if (monster.Movement.WaypointIndex >= monster.Movement.Waypoints.Count)
            FinishPath(monster);
    }
    private static float GetMonsterWaveSlowMultiplier(Monster monster, DateTime now)
    {
        return now < monster.WaveSlowUntilUtc ? OrbData.WaveSlowMoveSpeedMultiplier : 1f;
    }

    private void FinishPath(Monster monster)
    {
        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position)))
        {
            var areaCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monster.Area));
            monster.Movement.PositionCorrection = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, monster.Position, areaCenter, monster.Area);
        }

        if (monster.ChaseTargetPlayerId == 0 && monster.Movement.Waypoints.Count > 0)
        {
            var destination = monster.Movement.Waypoints[^1];
            monster.AnchorX = destination.X;
            monster.AnchorY = destination.Y;
        }
        else
        {
            monster.AnchorX = (monster.Movement.PositionCorrection ?? monster.Position).X;
            monster.AnchorY = (monster.Movement.PositionCorrection ?? monster.Position).Y;
        }

        if (monster.ChaseTargetPlayerId != 0)
        {
            monster.HomeArea = monster.Area;
        }
        monster.Movement.Waypoints.Clear();
        monster.Movement.WaypointIndex = 0;
        monster.Aggro = true;
    }

    public void PlanMovement(MatchRuntime runtime, Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants, DateTime now, bool holdAtThreshold)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster movement requires the match lock.");
        }
        monster.Movement.ResetIntent();
        if (runtime.IsEnded || !monster.Alive || now < monster.ActivatesAtUtc)
        {
            return;
        }
        if (monster.Movement.Waypoints.Count > 0)
        {
            bool switchToTarget = !holdAtThreshold && TryAcquireNearbyTarget(monster, participants);
            if (!switchToTarget && !holdAtThreshold && monster.ChaseTargetPlayerId != 0)
            {
                switchToTarget = CanReachNearestParticipant(monster, participants);
            }
            if (!switchToTarget)
            {
                PlanPathMovement(monster, now, holdAtThreshold);
                return;
            }
            FinishPath(monster);
        }

        if (!monster.Aggro && !TryAcquireNearbyTarget(monster, participants))
        {
            return;
        }

        bool found = TrySelectMovementTarget(runtime, monster, participants, now, out var target);

        if (!found && TryStartCrossAreaPursuit(monster, participants))
        {
            PlanPathMovement(monster, now, holdAtThreshold);
            return;
        }

        if (!found)
        {
            PlanReturnOrPatrol(monster, now);
            return;
        }

        monster.ChaseTargetPlayerId = target.PlayerId;
        if (monster.AttackRangeValue > Monster.BaseContactRadius)
        {
            float holdRange = monster.AttackRangeValue * Config.SWARM_MONSTER_RANGED_HOLD_RANGE_RATIO;
            if (GroundGeometry.IsWithinGroundRadius(monster.Position, target.Position, holdRange))
            {
                return;
            }
        }

        monster.Movement.PlanPathToTarget = true;
        PlanDirectMovement(monster, target.Position, now);
    }

    private bool TrySelectMovementTarget(MatchRuntime runtime, Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants, DateTime now, out PlayerPositionSnapshot target)
    {
        bool found = false;
        target = default;
        float intruderNearestSquared = Config.SWARM_MONSTER_AGGRO_RADIUS * Config.SWARM_MONSTER_AGGRO_RADIUS;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
            {
                continue;
            }
            float intruderDx = participant.Position.X - monster.Position.X;
            float intruderDy = participant.Position.Y - monster.Position.Y;
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
            if (participant.Area != monster.Area || participant.PlayerId != chaseId)
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
                if (participant.Area != monster.Area)
                {
                    continue;
                }
                float dx = participant.Position.X - monster.Position.X;
                float dy = participant.Position.Y - monster.Position.Y;
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

    private bool TryStartCrossAreaPursuit(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        if (monster.ChaseTargetPlayerId != 0 && monster.Movement.Waypoints.Count == 0)
        {
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.PlayerId != monster.ChaseTargetPlayerId || participant.Area == AreaType.None || participant.Area == monster.Area)
                {
                    continue;
                }

                if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, monster.Area, monster.Position, participant.Area, participant.Position, null, out var route))
                {
                    break;
                }
                monster.Movement.Waypoints.Clear();
                monster.Movement.Waypoints.AddRange(route);
                monster.Movement.WaypointIndex = 0;
                return true;
            }
        }

        return false;
    }

    private void PlanReturnOrPatrol(Monster monster, DateTime now)
    {
        var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
        float homeDx = anchor.X - monster.Position.X;
        float homeDy = anchor.Y - monster.Position.Y;
        float returnRadius = Config.SWARM_MONSTER_IDLE_PATROL_RADIUS * 2f;
        if (homeDx * homeDx + homeDy * homeDy <= returnRadius * returnRadius)
        {
            monster.ChaseTargetPlayerId = 0;
            double patrolSeconds = (now - monster.SpawnedAtUtc).TotalSeconds;
            float patrolAngle = monster.ScatterAngle + (float)(patrolSeconds * Config.SWARM_MONSTER_IDLE_PATROL_ANGULAR_SPEED);
            var patrolPoint = new Vector3f(anchor.X + MathF.Cos(patrolAngle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS, anchor.Y + MathF.Sin(patrolAngle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS, 0f);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, patrolPoint)))
            {
                patrolPoint = anchor;
            }
            PlanDirectMovement(monster, patrolPoint, now);
            return;
        }
        PlanDirectMovement(monster, anchor, now);
        return;
    }

    private bool TryReassignOwner(MatchRuntime runtime, Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants, out PlayerPositionSnapshot target)
    {
        target = default;
        bool found = false;
        long departedOwnerId = monster.OwnerPlayerId;
        monster.OwnerPlayerId = 0;
        long reassigned = 0;
        int leastLoad = int.MaxValue;
        bool reassignedOrbless = false;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
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

    private bool CanReachNearestParticipant(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        var nearestParticipant = default(PlayerPositionSnapshot);
        float nearestLineSquared = float.MaxValue;
        bool nearestFound = false;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
            {
                continue;
            }
            float lineDx = participant.Position.X - monster.Position.X;
            float lineDy = participant.Position.Y - monster.Position.Y;
            float lineSquared = lineDx * lineDx + lineDy * lineDy;
            if (lineSquared >= nearestLineSquared)
            {
                continue;
            }
            nearestLineSquared = lineSquared;
            nearestParticipant = participant;
            nearestFound = true;
        }
        return nearestFound && MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, monster.Position, nearestParticipant.Position);
    }

    private void PlanDirectMovement(Monster monster, Vector3f target, DateTime now)
    {
        monster.Movement.DirectTarget = target;
        monster.Movement.Speed = Config.SWARM_MONSTER_MOVE_SPEED * GetMonsterWaveSlowMultiplier(monster, now);
    }
}
