using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

/// <summary>
///     몬스터의 행군·추격·배회 이동과 벽에 갇힌 위치의 보정을 처리한다.
///     상태는 매치별 Monster에 보관한다.
/// </summary>
internal sealed class MonsterMovementService
{
    /// <summary>행군 예산 = 경로 실거리를 이속으로 나눈 시간 × 여유, 최소치 보장. 예산이 다한 행군은 걷어낸다.</summary>
    public static double ComputeMarchBudgetSeconds(Vector3f start, IReadOnlyList<Vector3f> route)
    {
        double length = 0d;
        var previous = start;
        for (int index = 0; index < route.Count; index++)
        {
            float dx = route[index].X - previous.X;
            float dy = route[index].Y - previous.Y;
            length += MathF.Sqrt(dx * dx + dy * dy);
            previous = route[index];
        }

        return Math.Max(Config.SWARM_MONSTER_MARCH_BUDGET_MINIMUM_SECONDS, length / Config.SWARM_MONSTER_MOVE_SPEED * Config.SWARM_MONSTER_MARCH_BUDGET_SLACK_MULTIPLIER);
    }

    private static bool HasDirectLineToParticipant(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        var nearest = default(PlayerPositionSnapshot);
        float nearestSquared = float.MaxValue;
        bool found = false;
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
            nearest = participant;
            found = true;
        }

        return found && MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, monster.Position, nearest.Position);
    }

    private static long ClaimLeastLoadedOwner(MatchRuntime runtime, AreaType area, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        var state = runtime.Monsters;
        long chosen = 0;
        int least = int.MaxValue;
        bool chosenOrbless = false;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != area)
            {
                continue;
            }

            int load = 0;
            foreach (var candidate in state.Entities.Values)
            {
                if (candidate.Alive && candidate.OwnerPlayerId == participant.PlayerId)
                {
                    load++;
                }
            }

            bool orbless = !runtime.GetOrbs(participant.PlayerId).HasAnyOrb();
            if (chosenOrbless && !orbless)
            {
                continue;
            }
            if (orbless && !chosenOrbless)
            {
                chosenOrbless = true;
                least = load;
                chosen = participant.PlayerId;
                continue;
            }

            if (load >= least)
            {
                continue;
            }
            least = load;
            chosen = participant.PlayerId;
        }

        return chosen;
    }

    private static bool HasParticipantWithinAggro(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
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

    private static bool TryStartCrossAreaPursuit(Monster monster, IReadOnlyList<PlayerPositionSnapshot> participants)
    {
        if (monster.ChaseTargetPlayerId == 0 || monster.Infiltrating)
        {
            return false;
        }

        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.PlayerId != monster.ChaseTargetPlayerId || participant.Area == AreaType.None || participant.Area == monster.Area)
            {
                continue;
            }

            if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, monster.Area, monster.Position, participant.Area, participant.Position, null, out var route))
            {
                return false;
            }

            monster.Infiltrating = true;
            monster.MarchIsPursuit = true;
            monster.MarchWaypoints.Clear();
            monster.MarchWaypoints.AddRange(route);
            monster.MarchIndex = 0;
            monster.MarchBudgetSeconds = ComputeMarchBudgetSeconds(monster.Position, route);
            return true;
        }

        return false;
    }

    private static void AdvanceInfiltration(Monster monster, double deltaSeconds, DateTime now, bool holdAtThreshold = false)
    {
        if (holdAtThreshold && monster.Area == monster.HomeArea)
        {
            return;
        }

        monster.MarchBudgetSeconds -= deltaSeconds;
        float remaining = (float)(Config.SWARM_MONSTER_MOVE_SPEED * monster.MarchSpeedScale * GetMonsterWaveSlowMultiplier(monster, now) * deltaSeconds);
        while (remaining > 0f && monster.MarchIndex < monster.MarchWaypoints.Count)
        {
            var waypoint = ResolveLaneWaypoint(monster);
            float dx = waypoint.X - monster.Position.X;
            float dy = waypoint.Y - monster.Position.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= Config.SWARM_MONSTER_MARCH_WAYPOINT_ARRIVE_DISTANCE)
            {
                monster.MarchIndex++;
                continue;
            }

            float step = Math.Min(remaining, distance);
            var proposed = new Vector3f(monster.Position.X + dx / distance * step, monster.Position.Y + dy / distance * step, 0f);

            if (holdAtThreshold && GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed)) == monster.HomeArea)
            {
                return;
            }

            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed)) &&
                !GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, waypoint)))
            {
                monster.MarchIndex++;
                continue;
            }

            monster.Position = proposed;
            remaining -= step;
        }

        monster.Area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position));
        if (monster.Area == monster.HomeArea)
        {
            if (!holdAtThreshold)
            {
                ArriveFromInfiltration(monster);
            }
            return;
        }

        if (holdAtThreshold || (monster.MarchIndex < monster.MarchWaypoints.Count && monster.MarchBudgetSeconds > 0d))
        {
            return;
        }

        if (monster.MarchIsPursuit)
        {
            ArriveFromInfiltration(monster);
            return;
        }

        monster.Alive = false;
        monster.DiedAtUtc = now;
    }

    private static float GetMonsterWaveSlowMultiplier(Monster monster, DateTime now)
    {
        return now < monster.WaveSlowUntilUtc ? OrbData.WaveSlowMoveSpeedMultiplier : 1f;
    }

    public void RescueMonsterFromBlockedCell(Monster monster)
    {
        if (monster.Infiltrating)
        {
            return;
        }

        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position)))
        {
            return;
        }

        var areaCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monster.Area));
        var rescued = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, monster.Position, areaCenter, monster.Area);
        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, rescued)))
        {
            rescued = MapPathfinder.ClampToWalkable(Config.SWARM_MATCH_MAP, monster.Position, areaCenter);
        }

        monster.Position = rescued;
    }

    private static Vector3f ResolveLaneWaypoint(Monster monster)
    {
        var waypoint = monster.MarchWaypoints[monster.MarchIndex];
        if (MathF.Abs(monster.MarchLaneOffset) < 0.01f || monster.MarchIndex >= monster.MarchWaypoints.Count - 1)
        {
            return waypoint;
        }

        var from = monster.MarchIndex == 0 ? monster.Position : monster.MarchWaypoints[monster.MarchIndex - 1];
        float dx = waypoint.X - from.X;
        float dy = waypoint.Y - from.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.05f)
        {
            return waypoint;
        }

        var offset = new Vector3f(waypoint.X - dy / length * monster.MarchLaneOffset, waypoint.Y + dx / length * monster.MarchLaneOffset, 0f);
        return GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, offset)) ? offset : waypoint;
    }

    private static void ArriveFromInfiltration(Monster monster)
    {
        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position)))
        {
            var areaCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monster.Area));
            monster.Position = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, monster.Position, areaCenter, monster.Area);
        }

        if (!monster.MarchIsPursuit && monster.MarchWaypoints.Count > 0)
        {
            var destination = monster.MarchWaypoints[^1];
            monster.AnchorX = destination.X;
            monster.AnchorY = destination.Y;
        }
        else
        {
            monster.AnchorX = monster.Position.X;
            monster.AnchorY = monster.Position.Y;
        }

        if (monster.MarchIsPursuit)
        {
            monster.HomeArea = monster.Area;
        }

        monster.Infiltrating = false;
        monster.MarchIsPursuit = false;
        monster.MarchWaypoints.Clear();
        monster.MarchIndex = 0;
        monster.Aggro = true;
    }

    public void Move(MatchRuntime runtime, Monster monster, DateTime now, double deltaSeconds, bool holdAtThreshold)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster movement requires the match lock.");
        }
        var state = runtime.Monsters;
        IReadOnlyList<PlayerPositionSnapshot> participants = state.LastParticipants;
        if (monster.Infiltrating)
        {
            bool leaveMarch = !holdAtThreshold && (HasParticipantWithinAggro(monster, participants) || (monster.MarchIsPursuit && HasDirectLineToParticipant(monster, participants)));
            if (!leaveMarch)
            {
                AdvanceInfiltration(monster, deltaSeconds, now, holdAtThreshold);
                return;
            }
            ArriveFromInfiltration(monster);
        }

        if (!monster.Aggro && !HasParticipantWithinAggro(monster, participants))
        {
            return;
        }

        bool found = false;
        var target = default(PlayerPositionSnapshot);
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
            long departedOwnerId = monster.OwnerPlayerId;
            monster.OwnerPlayerId = 0;
            long reassigned = ClaimLeastLoadedOwner(runtime, monster.Area, participants);
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

        if (!found && TryStartCrossAreaPursuit(monster, participants))
        {
            AdvanceInfiltration(monster, deltaSeconds, now);
            return;
        }

        if (!found)
        {
            var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
            float homeDx = anchor.X - monster.Position.X;
            float homeDy = anchor.Y - monster.Position.Y;
            if (homeDx * homeDx + homeDy * homeDy <= Config.SWARM_MONSTER_IDLE_PATROL_RADIUS * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS * 4f)
            {
                monster.ChaseTargetPlayerId = 0;
                double patrolSeconds = (now - monster.SpawnedAtUtc).TotalSeconds;
                float patrolAngle = monster.ScatterAngle + (float)(patrolSeconds * Config.SWARM_MONSTER_IDLE_PATROL_ANGULAR_SPEED);
                var patrolPoint = new Vector3f(anchor.X + MathF.Cos(patrolAngle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS, anchor.Y + MathF.Sin(patrolAngle) * Config.SWARM_MONSTER_IDLE_PATROL_RADIUS, 0f);
                if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, patrolPoint)))
                {
                    patrolPoint = anchor;
                }
                MoveTowardPlayer(monster, patrolPoint, deltaSeconds, now);
                return;
            }
            MoveTowardPlayer(monster, anchor, deltaSeconds, now);
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

        if (now >= monster.NextChasePlanAtUtc)
        {
            monster.NextChasePlanAtUtc = now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
            bool direct = MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, monster.Position, target.Position);
            if (!direct && MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, monster.Area, monster.Position, target.Area, target.Position, null, out var detour))
            {
                monster.Infiltrating = true;
                monster.MarchIsPursuit = true;
                monster.MarchWaypoints.Clear();
                monster.MarchWaypoints.AddRange(detour);
                monster.MarchIndex = 0;
                monster.MarchBudgetSeconds = ComputeMarchBudgetSeconds(monster.Position, detour);
                return;
            }
        }

        MoveTowardPlayer(monster, target.Position, deltaSeconds, now);
    }

    private static void MoveTowardPlayer(Monster monster, Vector3f playerPosition, double deltaSeconds, DateTime now)
    {
        var target = playerPosition;
        float dx = target.X - monster.Position.X;
        float dy = target.Y - monster.Position.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance < 0.05f)
        {
            return;
        }

        float step = (float)(Config.SWARM_MONSTER_MOVE_SPEED * GetMonsterWaveSlowMultiplier(monster, now) * deltaSeconds);
        if (step > distance)
        {
            step = distance;
        }
        var proposed = new Vector3f(monster.Position.X + dx / distance * step, monster.Position.Y + dy / distance * step, 0f);
        var proposedCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed);
        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, proposedCell))
        {
            monster.Position = proposed;
            return;
        }

        var slideX = new Vector3f(proposed.X, monster.Position.Y, 0f);
        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, slideX)))
        {
            monster.Position = slideX;
            return;
        }

        var slideY = new Vector3f(monster.Position.X, proposed.Y, 0f);
        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, slideY)))
        {
            monster.Position = slideY;
        }
    }
}
