using game_server.players.bots;
using game_server.matches.monsters;
using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     전투 전에 봇·몬스터의 이동 단계를 실행한다. 카운트다운 중에는 몬스터만 이동한다.
///     행동 서비스가 작성한 이동 명령을 공통 Move에서 실행하여 봇·몬스터의 공간 정보를 갱신한다.
///     개체별 경로는 MovementState가 소유하고 호출자는 매치 잠금을 보유한다.
/// </summary>
internal class MatchMovementService(
    BotBehaviorService botBehavior,
    MonsterBehaviorService monsterBehavior,
    MatchMonsterSpawnService monsterSpawns)
{
    public virtual void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement tick requires the match lock.");
        }
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation)
        {
            return;
        }
        bool isGameplayActive = runtime.IsGameplayActive(nowUtc);
        if (isGameplayActive && runtime.Bots.HasBots())
        {
            var activeBots = new List<Bot>();
            foreach (var bot in runtime.Bots.GetBots())
            {
                if (!bot.Player.IsEliminated)
                {
                    activeBots.Add(bot);
                }
            }
            var movements = new List<BotMovementResult>();
            long planningBotId = activeBots.Count > 0 ? runtime.Bots.SelectMovementPlanningBot(activeBots) : 0;
            var now = nowUtc;
            foreach (var bot in activeBots)
            {
                if (!bot.Player.IsSleeping)
                {
                    botBehavior.DecideMovement(runtime, bot.PlayerId);
                }
                botBehavior.PlanMovement(runtime, bot, now, bot.PlayerId == planningBotId);
                var intent = bot.Movement;
                float botDeltaSeconds = (float)(now - intent.LastProcessedAtUtc).TotalSeconds;
                intent.LastProcessedAtUtc = now;
                if (botDeltaSeconds <= 0f)
                {
                    botDeltaSeconds = 0.25f;
                }
                if (bot.Player.Position == null || bot.Player.IsEliminated)
                {
                    intent.ResetIntent();
                    continue;
                }

                if (bot.Player.IsSleeping)
                {
                    intent.ResetIntent();
                }
                var fromArea = bot.Player.CurrentArea;
                bool attemptedDodge = intent.DodgeDirection != null;
                var moved = Move(runtime, bot.Player.Profile.ObjectInfo, intent, botDeltaSeconds,
                    canEnter: candidate =>
                    {
                        var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate);
                        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
                        return !BotBehaviorService.IsUnsafeStep(runtime, bot, cell, area, now);
                    },
                    canDodge: candidate =>
                    {
                        var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate);
                        return GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) &&
                            GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == fromArea &&
                            !BotBehaviorService.IsUnsafeStep(runtime, bot, cell, fromArea, now);
                    });
                if (attemptedDodge)
                {
                    botBehavior.CompleteDodge(bot, moved.DodgeDirection);
                }

                if (moved.PathBlocked)
                {
                    botBehavior.HandleBlockedPath(bot, now);
                }
                var objectInfo = bot.Player.Profile.ObjectInfo;
                var resolvedArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, objectInfo.Cell);
                if (resolvedArea != AreaType.None)
                {
                    bot.Player.CurrentArea = resolvedArea;
                }
                if (moved.Changed)
                {
                    movements.Add(new BotMovementResult
                    {
                        BotPlayerId = bot.PlayerId, FromArea = fromArea, ToArea = bot.Player.CurrentArea,
                        ToCell = objectInfo.Cell, Position = objectInfo.Position,
                        Velocity = objectInfo.Velocity, Rotation = objectInfo.Rotation,
                        IsAreaTransition = fromArea != bot.Player.CurrentArea
                    });
                }
            }

            foreach (var moved in movements)
            {
                runtime.Bots.GetBot(moved.BotPlayerId)?.Player.AdvanceOrbOrbit(moved.Position);
            }
            BotMovementPublisher.SendMovements(runtime, movements);
        }
        var participants = new List<PlayerPositionSnapshot>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.Position != null)
            {
                participants.Add(new PlayerPositionSnapshot(player.PlayerId, player.CurrentArea, player.Position));
            }
        }
        if (participants.Count == 0)
        {
            return;
        }
        var state = runtime.Monsters;
        state.Initialize(nowUtc);
        double deltaSeconds = Math.Clamp((nowUtc - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
        state.LastTickAtUtc = nowUtc;
        if ((nowUtc - state.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
        {
            deltaSeconds *= Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        }
        bool preMatch = !isGameplayActive;
        monsterSpawns.ProcessSupply(runtime, participants, nowUtc, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc)
            {
                continue;
            }
            monsterBehavior.PlanMovement(runtime, monster, participants, nowUtc, preMatch);
            var intent = monster.Movement;
            PlanTargetPath(runtime, monster.Info.ObjectInfo, intent, nowUtc);
            bool followingPath = intent.FollowPath;
            var movementResult = Move(runtime, monster.Info.ObjectInfo, intent, (float)deltaSeconds,
                ignoreClosedDoors: true,
                canEnter: candidate => !followingPath || !preMatch || GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate)) != monster.HomeArea);
            if (movementResult.PathBlocked && !preMatch)
            {
                ReplanBlockedPath(runtime, monster, nowUtc);
            }
            var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, monster.Info.ObjectInfo.Cell);
            if (followingPath || area != AreaType.None)
            {
                monster.Area = area;
            }

            if (followingPath)
            {
                monsterBehavior.CompleteMovement(runtime, monster, preMatch);
            }

            if (intent.PositionCorrection != null)
            {
                monster.Position = intent.PositionCorrection;
            }
            RescueMonsterFromBlockedCell(runtime, monster);
            monster.Info.ObjectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position);
            intent.ResetIntent();
        }
        state.RemoveExpiredDead(nowUtc);
    }

    private static void RescueMonsterFromBlockedCell(MatchRuntime runtime, Monster monster)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster movement requires the match lock.");
        }
        if (monster.Movement.Waypoints.Count > 0)
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

    internal readonly record struct MovementResult(bool Changed, bool PathBlocked, Vector3f? DodgeDirection);

    internal static MovementResult Move(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState intent, float deltaSeconds, bool ignoreClosedDoors = false, Func<Vector3f, bool>? canEnter = null, Func<Vector3f, bool>? canDodge = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }

        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }

        try
        {
            var previous = intent.PositionCorrection ?? objectInfo.Position;
            Vector3f? next = null;
            Vector3f? dodgeDirection = null;
            bool pathBlocked = false;
            bool reachedDestination = false;
            float distance = intent.Speed * deltaSeconds;
            if (distance > 0f && intent.DodgeDirection != null)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    float sign = attempt == 0 ? 1f : -1f;
                    var direction = new Vector3f(intent.DodgeDirection.X * sign, intent.DodgeDirection.Y * sign, 0f);
                    var normalized = direction.Normalized();
                    var path = new MovementState();
                    path.Waypoints.Add(new Vector3f(previous.X + normalized.X * distance, previous.Y + normalized.Y * distance, 0f));
                    var candidate = AdvanceRoute(runtime, path, previous, distance, ignoreClosedDoors);
                    if (candidate.Equals(previous) || (canDodge != null && !canDodge(candidate)))
                    {
                        continue;
                    }
                    next = candidate;
                    dodgeDirection = direction;
                    break;
                }
            }
            if (next == null && distance > 0f && (intent.FollowPath || intent.DirectTarget != null))
            {
                var path = intent;
                if (intent.DirectTarget != null)
                {
                    path = new MovementState();
                    path.Waypoints.Add(intent.DirectTarget);
                }
                next = AdvanceRoute(runtime, path, previous, distance, ignoreClosedDoors, canEnter);
                pathBlocked = next.Equals(previous);
                reachedDestination = path.WaypointIndex >= path.Waypoints.Count;
            }
            next ??= previous;
            bool moved = !next.Equals(previous);
            var velocity = moved && !reachedDestination
                ? new Vector3f((next.X - previous.X) / deltaSeconds, (next.Y - previous.Y) / deltaSeconds, 0f)
                : new Vector3f();
            bool changed = !next.Equals(objectInfo.Position) || !velocity.Equals(objectInfo.Velocity);
            if (!next.Equals(objectInfo.Position))
            {
                objectInfo.Position = next;
            }
            objectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
            objectInfo.Velocity = velocity;
            if (velocity.X > 0.1f)
            {
                objectInfo.Rotation = 180f;
            }
            else if (velocity.X < -0.1f)
            {
                objectInfo.Rotation = 0f;
            }
            return new MovementResult(changed, pathBlocked, dodgeDirection);
        }
        finally
        {
            intent.ResetIntent();
        }
    }

    internal static Vector3f AdvanceRoute(MatchRuntime runtime, MovementState movement, Vector3f position, float distanceBudget, bool ignoreClosedDoors = false, Func<Vector3f, bool>? canEnter = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }
        var mapId = Config.SWARM_MATCH_MAP;
        var route = movement.Waypoints;
        int index = movement.WaypointIndex;
        var current = new Vector3f(position.X, position.Y, 0f);
        while (distanceBudget > 0f && index < route.Count)
        {
            var target = route[index];
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance == 0f)
            {
                index++;
                continue;
            }

            var fromCell = MapCoordinateConverter.WorldToCell(mapId, current);
            var targetCell = MapCoordinateConverter.WorldToCell(mapId, target);
            var fromArea = GameMapData.GetCurrentArea(mapId, fromCell);
            var targetArea = GameMapData.GetCurrentArea(mapId, targetCell);
            var door = GameDoorData.GetDoorForTransition(fromArea, targetArea, fromCell, targetCell);
            if (door != null && !ignoreClosedDoors && !runtime.Doors.IsDoorOpen(door.DoorId))
            {
                break;
            }
            if (canEnter != null && !canEnter(target))
            {
                break;
            }
            if (!MapPathfinder.IsSegmentWalkable(mapId, current, target))
            {
                break;
            }

            float step = Math.Min(distanceBudget, distance);
            var next = Vector3f.MoveTowardsXY(current, target, step);
            if (canEnter != null && !canEnter(next))
            {
                break;
            }
            current = next;
            distanceBudget -= step;
            if (step == distance)
            {
                current = new Vector3f(target.X, target.Y, 0f);
                index++;
            }
        }
        movement.WaypointIndex = index;
        return current;
    }

    private static void ReplanBlockedPath(MatchRuntime runtime, Monster monster, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster path planning requires the match lock.");
        }

        if (runtime.IsEnded || monster.Movement.Waypoints.Count == 0 || now < monster.Movement.NextPathPlanAtUtc)
        {
            return;
        }
        monster.Movement.NextPathPlanAtUtc = now.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var destination = monster.Movement.Waypoints[^1];
        var destinationArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination));
        if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, monster.Area, monster.Position, destinationArea, destination, null, out var route))
        {
            return;
        }
        monster.Movement.Waypoints.Clear();
        monster.Movement.Waypoints.AddRange(route);
        monster.Movement.WaypointIndex = 0;
    }

    internal static void PlanTargetPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Path planning requires the match lock.");
        }
        if (runtime.IsEnded || !movement.PlanPathToTarget || movement.DirectTarget == null || nowUtc < movement.NextPathPlanAtUtc)
        {
            return;
        }
        movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var target = movement.DirectTarget;
        if (MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, objectInfo.Position, target))
        {
            return;
        }

        var targetCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target);
        var targetArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, targetCell);
        if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Position, targetArea, target, null, out var path))
        {
            return;
        }

        movement.Waypoints.Clear();
        movement.Waypoints.AddRange(path);
        movement.WaypointIndex = 0;
        movement.ResetIntent();
    }

    internal static bool TryPlanCellPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, AreaType destinationArea, Cell destinationCell)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Path planning requires the match lock.");
        }

        if (runtime.IsEnded)
        {
            return false;
        }

        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, destinationArea, destinationCell);
        if (path == null || path.Count == 0)
        {
            return false;
        }

        movement.Clear();
        foreach (var step in path)
        {
            movement.Waypoints.Add(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, step.Cell));
        }
        return true;
    }
}
