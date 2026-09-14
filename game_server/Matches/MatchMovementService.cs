using game_server.players.bots;
using game_server.matches.monsters;
using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.helpers;
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
        var targets = new List<MovementTarget>();
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
                if (intent.PathRequest == MovementPathRequest.CellPath && intent.Destination != null)
                {
                    if (!TryPlanCellPath(runtime, bot.Player.GameInfo.ObjectInfo, intent, intent.DestinationArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, intent.Destination)))
                    {
                        intent.Clear();
                    }
                }
                foreach (var targetCell in botBehavior.GetIdleWanderTargets(runtime, bot, now))
                {
                    if (TryPlanCellPath(runtime, bot.Player.GameInfo.ObjectInfo, intent, bot.Player.CurrentArea, targetCell))
                    {
                        break;
                    }
                }
                botBehavior.ConfigureMovement(runtime, bot, now);
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
                targets.Add(new MovementTarget(bot.Player.GameInfo.ObjectInfo, intent, botDeltaSeconds, false, bot, null, bot.Player.CurrentArea, intent.DodgeDirection != null, false));
            }
        }

        var participants = new List<PlayerPositionSnapshot>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.Position != null)
            {
                participants.Add(new PlayerPositionSnapshot(player.PlayerId, player.CurrentArea, player.Position));
            }
        }

        bool preMatch = !isGameplayActive;
        if (participants.Count > 0)
        {
            var state = runtime.Monsters;
            state.Initialize(nowUtc);
            double deltaSeconds = Math.Clamp((nowUtc - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
            state.LastTickAtUtc = nowUtc;
            if ((nowUtc - state.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS)
            {
                deltaSeconds *= Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
            }
            monsterSpawns.ProcessSupply(runtime, participants, nowUtc, preMatch);
            foreach (var monster in state.Entities.Values)
            {
                if (!monster.Alive || nowUtc < monster.ActivatesAtUtc)
                {
                    continue;
                }
                monsterBehavior.PlanMovement(runtime, monster, participants, nowUtc, preMatch);
                var intent = monster.Movement;
                if (intent.PathRequest == MovementPathRequest.WorldPath && intent.Destination != null)
                {
                    if (!TryPlanWorldPath(runtime, monster.Info.ObjectInfo, intent, intent.DestinationArea, intent.Destination))
                    {
                        intent.FollowPath = false;
                        intent.StopBeforeArea = null;
                        intent.PathRequest = MovementPathRequest.None;
                        monsterBehavior.PlanReturnOrPatrol(runtime, monster, nowUtc);
                    }
                }
                PlanTargetPath(runtime, monster.Info.ObjectInfo, intent, nowUtc);
                targets.Add(new MovementTarget(monster.Info.ObjectInfo, intent, (float)deltaSeconds, true, null, monster, monster.Area, false, intent.FollowPath));
            }
        }

        foreach (var target in targets)
        {
            target.Result = Move(runtime, target.ObjectInfo, target.Intent, target.DeltaSeconds, target.IgnoreClosedDoors, nowUtc);
        }

        var movements = new List<BotMovementResult>();
        foreach (var target in targets)
        {
            var moved = target.Result;
            if (target.Bot is { } bot)
            {
                if (target.AttemptedDodge)
                {
                    botBehavior.CompleteDodge(bot, moved.DodgeDirection);
                }

                if (moved.PathBlocked)
                {
                    botBehavior.HandleBlockedPath(bot, nowUtc);
                }

                if (!moved.Changed)
                {
                    continue;
                }

                var info = target.ObjectInfo;
                bot.Player.AdvanceOrbOrbit(info.Position);
                movements.Add(new BotMovementResult
                {
                    BotPlayerId = bot.PlayerId, FromArea = target.FromArea, ToArea = info.Area,
                    ToCell = info.Cell, Position = info.Position, Velocity = info.Velocity,
                    Rotation = info.Rotation, IsAreaTransition = target.FromArea != info.Area
                });
            }
            else if (target.Monster is { } monster)
            {
                if (moved.PathBlocked && !preMatch)
                {
                    ReplanBlockedPath(runtime, monster, nowUtc);
                }

                if (target.FollowingPath)
                {
                    monsterBehavior.CompleteMovement(runtime, monster, preMatch);
                }
            }
        }

        if (isGameplayActive && runtime.Bots.HasBots())
        {
            BotMovementPublisher.SendMovements(runtime, movements);
        }

        if (participants.Count > 0)
        {
            runtime.Monsters.RemoveExpiredDead(nowUtc);
        }
    }

    private sealed record MovementTarget(GameObjectInfo ObjectInfo, MovementState Intent, float DeltaSeconds, bool IgnoreClosedDoors, Bot? Bot, Monster? Monster, AreaType FromArea, bool AttemptedDodge, bool FollowingPath)
    {
        public MovementResult Result { get; set; }
    }

    internal readonly record struct MovementResult(bool Changed, bool PathBlocked, Vector3f? DodgeDirection);

    internal static MovementResult Move(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState intent, float deltaSeconds, bool ignoreClosedDoors = false, DateTime? nowUtc = null)
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
            var previous = objectInfo.Position;
            var now = nowUtc ?? DateTime.UtcNow;
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
                    var path = new MovementState { StopBeforeArea = intent.StopBeforeArea };
                    path.Waypoints.Add(new Vector3f(previous.X + normalized.X * distance, previous.Y + normalized.Y * distance, 0f));
                    var candidate = AdvanceRoute(runtime, path, previous, distance, ignoreClosedDoors, now);
                    var candidateCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate);
                    var sourceArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP,
                        MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, previous));
                    if (candidate.Equals(previous) || !GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, candidateCell) ||
                        GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidateCell) != sourceArea)
                    {
                        continue;
                    }
                    next = candidate;
                    dodgeDirection = direction;
                    break;
                }
            }
            if (next == null && distance > 0f && (intent.FollowPath || (intent.MoveToDestination && intent.Destination != null)))
            {
                var path = intent;
                if (intent.MoveToDestination && intent.Destination != null)
                {
                    path = new MovementState { StopBeforeArea = intent.StopBeforeArea };
                    path.Waypoints.Add(intent.Destination);
                }
                next = AdvanceRoute(runtime, path, previous, distance, ignoreClosedDoors, now);
                pathBlocked = next.Equals(previous);
                reachedDestination = path.WaypointIndex >= path.Waypoints.Count;
            }
            next ??= previous;
            bool corrected = false;
            var nextCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, next);
            if (intent.WaypointIndex >= intent.Waypoints.Count && !GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, nextCell))
            {
                var areaCenter = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, objectInfo.Area));
                var rescued = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, next, areaCenter, objectInfo.Area);
                if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, rescued)))
                {
                    rescued = MapPathfinder.ClampToWalkable(Config.SWARM_MATCH_MAP, next, areaCenter);
                }
                corrected = !rescued.Equals(next);
                next = rescued;
            }
            bool moved = !next.Equals(previous);
            var velocity = moved && !reachedDestination && !corrected ? new Vector3f((next.X - previous.X) / deltaSeconds, (next.Y - previous.Y) / deltaSeconds, 0f) : new Vector3f();
            bool changed = !next.Equals(objectInfo.Position) || !velocity.Equals(objectInfo.Velocity);
            if (!next.Equals(objectInfo.Position))
            {
                objectInfo.Position = next;
            }
            objectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
            var resolvedArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, objectInfo.Cell);
            if (resolvedArea != AreaType.None)
            {
                objectInfo.Area = resolvedArea;
            }
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

    internal static Vector3f AdvanceRoute(MatchRuntime runtime, MovementState movement, Vector3f position, float distanceBudget, bool ignoreClosedDoors = false, DateTime? nowUtc = null)
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
        var now = nowUtc ?? DateTime.UtcNow;
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
            var door = runtime.Doors.GetBlockingDoor(fromArea, targetArea, fromCell, targetCell, ignoreClosedDoors);
            if (door != null)
            {
                break;
            }
            if ((movement.StopBeforeArea.HasValue && targetArea == movement.StopBeforeArea.Value) ||
                IsUnsafeStep(runtime, position, GameMapData.GetCurrentArea(mapId,
                    MapCoordinateConverter.WorldToCell(mapId, position)), targetCell, targetArea, now))
            {
                break;
            }
            if (!GridMovementTraversal.IsTraversable(mapId, current, target))
            {
                break;
            }

            float step = Math.Min(distanceBudget, distance);
            var next = Vector3f.MoveTowardsXY(current, target, step);
            var nextCell = MapCoordinateConverter.WorldToCell(mapId, next);
            var nextArea = GameMapData.GetCurrentArea(mapId, nextCell);
            if ((movement.StopBeforeArea.HasValue && nextArea == movement.StopBeforeArea.Value) ||
                IsUnsafeStep(runtime, position, GameMapData.GetCurrentArea(mapId,
                    MapCoordinateConverter.WorldToCell(mapId, position)), nextCell, nextArea, now))
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
        TryPlanWorldPath(runtime, monster.Info.ObjectInfo, monster.Movement, destinationArea, destination);
    }

    internal static void PlanTargetPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Path planning requires the match lock.");
        }
        if (runtime.IsEnded || movement.PathRequest != MovementPathRequest.Detour || movement.Destination == null || nowUtc < movement.NextPathPlanAtUtc)
        {
            return;
        }
        movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var target = movement.Destination;
        if (GridMovementTraversal.IsTraversable(Config.SWARM_MATCH_MAP, objectInfo.Position, target))
        {
            return;
        }

        var targetCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target);
        var targetArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, targetCell);
        if (!TryPlanWorldPath(runtime, objectInfo, movement, targetArea, target))
        {
            return;
        }
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

    internal static bool TryPlanWorldPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement,
        AreaType destinationArea, Vector3f destination)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Path planning requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return false;
        }
        if (!MapPathfinder.TryPlanRoute(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Position,
                destinationArea, destination, null, out var path))
        {
            return false;
        }
        movement.Waypoints.Clear();
        movement.Waypoints.AddRange(path);
        movement.WaypointIndex = 0;
        return true;
    }

    internal static bool IsUnsafeStep(MatchRuntime runtime, Vector3f position, AreaType currentArea, Cell targetCell, AreaType targetArea, DateTime nowUtc)
    {
        if (targetArea != currentArea && runtime.Closures.IsAreaClosed(targetArea))
        {
            return true;
        }

        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        int targetDistance = SwarmPressureField.GetDistance(targetCell);
        if (targetDistance <= safeDistance)
        {
            return false;
        }

        var currentCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position);
        return targetDistance > SwarmPressureField.GetDistance(currentCell);
    }

}
