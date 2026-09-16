using game_server.players.bots;
using game_server.players;
using game_server.matches.monsters;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     전투 전에 봇·몬스터의 이동 단계를 실행한다. 카운트다운 중에는 봇·몬스터 모두 대기한다.
///     행동 서비스의 목표를 받아 경로 선택·재계획·이동 명령 준비·도착 처리를 수행한다.
///     봇·몬스터는 공통 Move에서 공간 정보를 갱신하고, 봇만 닫힌 문 앞에서 멈춘다.
///     개체별 경로는 MovementState가 소유하고 호출자는 매치 잠금을 보유한다.
/// </summary>
internal class MatchMoveService(
    BotBehaviorService botBehavior,
    MonsterBehaviorService monsterBehavior)
{
    // 지연된 틱에서도 봇·몬스터 모두 최대 0.25초치만 이동
    private const double MaxMovementDeltaSeconds = 0.25d;

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

        if (!runtime.IsGameplayActive(nowUtc))
        {
            return;
        }
        var movementActors = new List<MovementActor>();
        var activeBots = runtime.Bots.GetBots().FindAll((x) => !x.Player.IsEliminated);
        var participants = runtime.GetAlivePlayers().FindAll(player => player.Position != null);

        // 이동 타겟 선정
        foreach (var bot in activeBots)
        {
            var request = botBehavior.CreateMovementRequest(runtime, bot, nowUtc);
            movementActors.Add(new MovementActor(bot.Player.GameInfo.ObjectInfo, bot.Movement, false, request));
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            var request = monsterBehavior.CreateMovementRequest(runtime, monster, participants, nowUtc);
            movementActors.Add(new MovementActor(monster.Info.ObjectInfo, monster.Movement, true, request));
        }

        // 이동 준비
        foreach (var actor in movementActors)
        {
            PrepareMovement(runtime, actor.ObjectInfo, actor.Movement, actor.Request, nowUtc, actor.IgnoreClosedDoors);
        }

        // 이동
        foreach (var actor in movementActors)
        {
            actor.Result = Move(runtime, actor.ObjectInfo, actor.Movement, actor.Request, actor.IgnoreClosedDoors, nowUtc);
        }

        foreach (var actor in movementActors)
        {
            if (!actor.Result.Changed || actor.ObjectInfo.ObjectType != ObjectType.PLAYER)
            {
                continue;
            }
            var player = runtime.GetParticipant(actor.ObjectInfo.ObjectId);
            if (player != null)
            {
                PlayerMovementService.CompleteMovement(runtime, player);
            }
        }
    }

    internal static void PrepareMovement(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, MovementRequest request, DateTime nowUtc, bool ignoreClosedDoors = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement preparation requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }
        if (request.Speed <= 0f)
        {
            movement.Clear();
            movement.NextPathPlanAtUtc = DateTime.MinValue;
            return;
        }

        var destination = request.DestinationCell;
        if (destination == null)
        {
            return;
        }

        if (movement.WaypointIndex < movement.Waypoints.Count && !CanKeepPath(runtime, objectInfo, movement, ignoreClosedDoors))
        {
            movement.Waypoints.Clear();
            movement.WaypointIndex = 0;
            movement.NextPathPlanAtUtc = DateTime.MinValue;
        }

        // 재탐색 시점 전에는 기존 경로를 유지한다.
        if (nowUtc < movement.NextPathPlanAtUtc)
        {
            return;
        }

        // 경로 탐색을 매 틱 수행하지 않도록 다음 탐색 가능 시간 지정
        movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var destinationArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, destination);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, destinationArea, destination);
        if (path == null || path.Count == 0)
        {
            return;
        }

        var previousCell = objectInfo.Cell;
        var previousArea = objectInfo.Area;

        // 계산된 경로를 순회하며 닫힌 문이 있다면 문으로 목표 변경
        foreach (var step in path)
        {
            var nextArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, step.Cell);
            var door = ignoreClosedDoors ? null : runtime.Doors.GetBlockingDoor(previousArea, nextArea, previousCell, step.Cell);
            if (door != null)
            {
                var interaction = GameInteractableData.GetAll().FirstOrDefault(info => info.DoorId == door.DoorId && info.ZoneId == (int)previousArea);
                if (interaction == null)
                {
                    return;
                }
                path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, (AreaType)interaction.ZoneId, new Cell(interaction.CellX, interaction.CellY));
                if (path == null || path.Count == 0)
                {
                    return;
                }
                break;
            }
            previousCell = step.Cell;
            previousArea = nextArea;
        }

        previousCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
        foreach (var step in path)
        {
            if (!CanTraverse(runtime, previousCell, step.Cell, ignoreClosedDoors))
            {
                return;
            }
            previousCell = step.Cell;
        }

        movement.Waypoints.Clear();
        foreach (var step in path)
        {
            movement.Waypoints.Add(step.Cell.Clone());
        }
        movement.WaypointIndex = 0;
    }

    internal static MovementResult Move(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, MovementRequest request, bool ignoreClosedDoors = false, DateTime? nowUtc = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }

        var previous = objectInfo.Position;
        var now = nowUtc ?? DateTime.UtcNow;
        var lastProcessedAtUtc = movement.LastProcessedAtUtc;
        if (runtime.StartsAtUtc != null)
        {
            var startedAtUtc = runtime.StartsAtUtc.Value;
            if (lastProcessedAtUtc < startedAtUtc)
            {
                lastProcessedAtUtc = startedAtUtc;
            }
        }

        double elapsedSeconds = (now - lastProcessedAtUtc).TotalSeconds;
        float deltaSeconds = (float)Math.Clamp(elapsedSeconds, 0d, MaxMovementDeltaSeconds);
        movement.LastProcessedAtUtc = now;

        var next = previous;
        bool reachedPathEnd = false;
        // 속도가 0 이하면 정지하고 기존 경로는 유지한다.
        float distance = Math.Max(0f, request.Speed) * deltaSeconds;
        if (distance > 0f && movement.WaypointIndex < movement.Waypoints.Count)
        {
            next = MoveAlongPath(runtime, movement, previous, distance, now, ignoreClosedDoors);
            reachedPathEnd = movement.WaypointIndex >= movement.Waypoints.Count;
            if (!reachedPathEnd && next.Equals(previous))
            {
                // 경로 끝 도달
                movement.Waypoints.Clear();
                movement.WaypointIndex = 0;
                movement.NextPathPlanAtUtc = DateTime.MinValue;
            }
        }

        bool positionChanged = !next.Equals(previous);

        // 이동을 계속할 때만 속도 유지
        var velocity = new Vector3f();
        if (positionChanged && !reachedPathEnd)
        {
            float velocityX = (next.X - previous.X) / deltaSeconds;
            float velocityY = (next.Y - previous.Y) / deltaSeconds;
            velocity = new Vector3f(velocityX, velocityY, 0f);
        }

        // 위치가 그대로여도 이동에서 정지로 전환될 수 있음
        bool velocityChanged = !velocity.Equals(objectInfo.Velocity);
        bool changed = positionChanged || velocityChanged;
        if (positionChanged)
        {
            objectInfo.Position = next;
        }

        // 변경된 포지션에 따른 결과 반영
        objectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);

        // 속도·방향 반영
        objectInfo.Velocity = velocity;
        if (velocity.X > 0.1f)
        {
            objectInfo.Rotation = 180f;
        }
        else if (velocity.X < -0.1f)
        {
            objectInfo.Rotation = 0f;
        }

        // 완료한 경로 정리
        if (reachedPathEnd)
        {
            movement.Waypoints.Clear();
            movement.WaypointIndex = 0;
        }
        return new MovementResult(changed, reachedPathEnd);
    }

    internal static Vector3f MoveAlongPath(MatchRuntime runtime, MovementState movement, Vector3f position, float remainingDistance, DateTime nowUtc, bool ignoreClosedDoors = false)
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
        int index = movement.WaypointIndex;
        var current = position;
        while (remainingDistance > 0f && index < movement.Waypoints.Count)
        {
            var target = MapCoordinateConverter.CellToWorld(mapId, movement.Waypoints[index]);
            // 다음 경로까지 거리 계산
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distanceToWaypoint = MathF.Sqrt(dx * dx + dy * dy);
            if (distanceToWaypoint == 0f)
            {
                // 이동 불필요
                index++;
                continue;
            }
            // 다음 이동할 거리
            float moveDistance = Math.Min(remainingDistance, distanceToWaypoint);
            float directionX = dx / distanceToWaypoint;
            float directionY = dy / distanceToWaypoint;
            float moveX = directionX * moveDistance;
            float moveY = directionY * moveDistance;
            // 다음 이동 좌표
            var next = new Vector3f(current.X + moveX, current.Y + moveY, 0f);
            // 남은 이동 거리
            bool reachesWaypoint = remainingDistance >= distanceToWaypoint;
            if (reachesWaypoint)
            {
                next = new Vector3f(target.X, target.Y, 0f);
            }
            current = next;
            remainingDistance -= moveDistance;
            if (reachesWaypoint)
            {
                index++;
            }
        }
        movement.WaypointIndex = index;
        return current;
    }

    private static bool CanKeepPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, bool ignoreClosedDoors)
    {
        if (movement.WaypointIndex >= movement.Waypoints.Count)
        {
            return false;
        }
        var previous = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
        for (int i = movement.WaypointIndex; i < movement.Waypoints.Count; i++)
        {
            var next = movement.Waypoints[i];
            bool valid = CanTraverse(runtime, previous, next, ignoreClosedDoors);
            if (!valid)
            {
                return false;
            }
            previous = next;
        }
        return true;
    }

    internal static bool CanTraverse(MatchRuntime runtime, Cell from, Cell to, bool ignoreClosedDoors)
    {
        foreach (var step in MapTraversal.GetSteps(from, to))
        {
            if (step.Horizontal != null && step.Vertical != null)
            {
                bool horizontalOpen = GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, step.Horizontal) && CanCrossDoor(runtime, step.From, step.Horizontal, ignoreClosedDoors);
                bool verticalOpen = GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, step.Vertical) && CanCrossDoor(runtime, step.From, step.Vertical, ignoreClosedDoors);
                if (!horizontalOpen && !verticalOpen)
                {
                    return false;
                }
            }
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, step.To))
            {
                return false;
            }
            if (!CanCrossDoor(runtime, step.From, step.To, ignoreClosedDoors))
            {
                return false;
            }
        }
        return true;
    }

    private static bool CanCrossDoor(MatchRuntime runtime, Cell from, Cell to, bool ignoreClosedDoors)
    {
        var fromArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, from);
        var toArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, to);
        var blockingDoor = runtime.Doors.GetBlockingDoor(fromArea, toArea, from, to, ignoreClosedDoors);
        return blockingDoor == null;
    }

    internal static bool TryFindSafePath(MatchRuntime runtime, GameObjectInfo objectInfo, Cell destination, DateTime nowUtc, out List<MapPathfinder.Step> path)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement path selection requires the match lock.");
        }
        path = [];
        if (runtime.IsEnded)
        {
            return false;
        }
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, destination);
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        if (area == AreaType.None || runtime.Closures.IsAreaClosed(area) || SwarmPressureField.GetDistance(destination) > safeDistance)
        {
            return false;
        }
        var planned = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, area, destination);
        if (planned is not { Count: > 0 })
        {
            return false;
        }
        int previousDistance = SwarmPressureField.GetDistance(objectInfo.Cell);
        foreach (var step in planned)
        {
            int distance = SwarmPressureField.GetDistance(step.Cell);
            if (distance > safeDistance && distance > previousDistance)
            {
                return false;
            }
            previousDistance = distance;
        }
        path = planned;
        return true;
    }

}
