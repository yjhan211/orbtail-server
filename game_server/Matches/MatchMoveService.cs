using game_server.players.bots;
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
        var movementTargets = new List<MovementTarget>();
        var activeBots = runtime.Bots.GetBots().FindAll((x) => !x.Player.IsEliminated);
        var participants = runtime.GetAlivePlayers().FindAll(player => player.Position != null);

        // 이동 타겟 선정
        foreach (var bot in activeBots)
        {
            var request = botBehavior.CreateMovementRequest(runtime, bot, nowUtc);
            movementTargets.Add(new MovementTarget(bot.Player.GameInfo.ObjectInfo, bot.Movement, false, bot, null, bot.Player.CurrentArea, request));
        }
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            var request = monsterBehavior.CreateMovementRequest(runtime, monster, participants, nowUtc);
            movementTargets.Add(new MovementTarget(monster.Info.ObjectInfo, monster.Movement, true, null, monster, monster.Area, request));
        }

        // 이동 준비
        foreach (var target in movementTargets)
        {
            PrepareMovement(runtime, target.ObjectInfo, target.Movement, target.Request, nowUtc, target.IgnoreClosedDoors);
        }

        // 이동
        foreach (var target in movementTargets)
        {
            target.Result = Move(runtime, target.ObjectInfo, target.Movement, target.Request, target.IgnoreClosedDoors, nowUtc);
        }

        foreach (var target in movementTargets)
        {
            if (target.Bot is { } bot && target.Result.Changed)
            {
                bot.Player.AdvanceOrbOrbit(target.ObjectInfo.Position);
                botBehavior.CompleteMovement(runtime, bot);
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
        if (request.HoldPosition || request.Speed <= 0f)
        {
            return;
        }

        // 이번에 요청한 목적지
        var destination = request.DestinationCell;
        if (destination == null && movement.Waypoints.Count > 0)
        {
            // 요청 목적지가 없으면 기존 경로의 마지막 셀
            destination = movement.Waypoints[^1];
        }
        // 아직 목적지가 없으면 저장된 목적지 사용
        destination ??= movement.DestinationCell;
        if (destination == null)
        {
            // 사용할 목적지가 없으므로 이동 준비 종료
            return;
        }

        bool firstRequest = movement.DestinationCell == null;
        bool changed = firstRequest || !movement.DestinationCell!.Equals(destination);
        if (changed)
        {
            // 목적지 갱신
            movement.DestinationCell = destination.Clone();
            movement.DestinationArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, destination);
        }
        if (movement.WaypointIndex < movement.Waypoints.Count && !CanKeepPath(runtime, objectInfo, movement, ignoreClosedDoors, request.StopBeforeArea))
        {
            movement.Waypoints.Clear();
            movement.WaypointIndex = 0;
            movement.NextPathPlanAtUtc = DateTime.MinValue;
        }

        bool hasPath = movement.WaypointIndex < movement.Waypoints.Count;
        if (!hasPath && objectInfo.Cell.Equals(destination))
        {
            // 목적지 도착
            return;
        }

        // 목적지가 갱신 or 새 경로를 갱신해야 할 시점이 됐는지
        bool needsPlan = (firstRequest && !hasPath) || nowUtc >= movement.NextPathPlanAtUtc;
        if (!needsPlan)
        {
            return;
        }

        bool planned = TrySetMovementPath(runtime, objectInfo, movement, movement.DestinationArea, destination, nowUtc, ignoreClosedDoors);
        if (planned)
        {
            return;
        }
        if (!CanKeepPath(runtime, objectInfo, movement, ignoreClosedDoors, request.StopBeforeArea))
        {
            movement.Waypoints.Clear();
            movement.WaypointIndex = 0;
        }
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
        float distance = 0f;
        if (!request.HoldPosition)
        {
            // 정지 요청
            distance = Math.Max(0f, request.Speed) * deltaSeconds;
        }
        if (distance > 0f && movement.WaypointIndex < movement.Waypoints.Count)
        {
            next = MoveAlongPath(runtime, movement, previous, distance, now, ignoreClosedDoors, request.StopBeforeArea);
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
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, objectInfo.Cell);
        if (area != AreaType.None)
        {
            objectInfo.Area = area;
        }

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

    internal static bool TrySetMovementPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, AreaType destinationArea, Cell destinationCell, DateTime nowUtc, bool ignoreClosedDoors = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Path planning requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return false;
        }

        // 경로 탐색을 매 틱 수행하지 않도록 다음 탐색 가능 시간 지정
        movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, destinationArea, destinationCell);
        if (path == null || path.Count == 0)
        {
            return false;
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
                    return false;
                }
                path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, (AreaType)interaction.ZoneId, new Cell(interaction.CellX, interaction.CellY));
                if (path == null || path.Count == 0)
                {
                    return false;
                }
                break;
            }
            previousCell = step.Cell;
            previousArea = nextArea;
        }

        movement.Waypoints.Clear();
        foreach (var step in path)
        {
            movement.Waypoints.Add(step.Cell.Clone());
        }
        movement.WaypointIndex = 0;
        return true;
    }

    internal static Vector3f MoveAlongPath(MatchRuntime runtime, MovementState movement, Vector3f position, float remainingDistance, DateTime nowUtc, bool ignoreClosedDoors = false, AreaType? stopBeforeArea = null)
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
        var originCell = MapCoordinateConverter.WorldToCell(mapId, position);
        var originArea = GameMapData.GetCurrentArea(mapId, originCell);
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
            // 현재 셀
            var currentCell = MapCoordinateConverter.WorldToCell(mapId, current);
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
            var nextCell = MapCoordinateConverter.WorldToCell(mapId, next);
            if (!CanTraverse(runtime, currentCell, nextCell, ignoreClosedDoors, originArea, stopBeforeArea))
            {
                // 다음 셀이 막혀있으므로 정지
                break;
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

    private static bool CanKeepPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, bool ignoreClosedDoors, AreaType? stopBeforeArea)
    {
        if (movement.WaypointIndex >= movement.Waypoints.Count)
        {
            return false;
        }
        var previous = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
        var originArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, previous);
        for (int i = movement.WaypointIndex; i < movement.Waypoints.Count; i++)
        {
            var next = movement.Waypoints[i];
            bool valid = CanTraverse(runtime, previous, next, ignoreClosedDoors, originArea, stopBeforeArea);
            if (!valid)
            {
                return false;
            }
            previous = next;
        }
        return true;
    }

    internal static bool CanTraverse(MatchRuntime runtime, Cell from, Cell to, bool ignoreClosedDoors, AreaType? allowedClosedArea = null, AreaType? stopBeforeArea = null)
    {
        foreach (var step in MapTraversal.GetSteps(from, to))
        {
            if (step.Horizontal != null && step.Vertical != null)
            {
                // 대각선 통과 가능 여부
                bool horizontalOpen = CanEnterCell(runtime, step.Horizontal, allowedClosedArea, stopBeforeArea) && CanCrossDoor(runtime, step.From, step.Horizontal, ignoreClosedDoors);
                bool verticalOpen = CanEnterCell(runtime, step.Vertical, allowedClosedArea, stopBeforeArea) && CanCrossDoor(runtime, step.From, step.Vertical, ignoreClosedDoors);
                if (!horizontalOpen && !verticalOpen)
                {
                    return false;
                }
            }
            if (!CanEnterCell(runtime, step.To, allowedClosedArea, stopBeforeArea))
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

    private static bool CanEnterCell(MatchRuntime runtime, Cell cell, AreaType? allowedClosedArea, AreaType? stopBeforeArea)
    {
        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell))
        {
            return false;
        }
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
        if (area == stopBeforeArea || (area != allowedClosedArea && runtime.Closures.IsAreaClosed(area)))
        {
            return false;
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
        var blockedAreas = new HashSet<AreaType>();
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            if (region.AreaType == objectInfo.Area)
            {
                continue;
            }
            if (runtime.Closures.IsAreaClosed(region.AreaType))
            {
                blockedAreas.Add(region.AreaType);
            }
        }
        var planned = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell, area, destination, blockedAreas);
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

    private sealed record MovementTarget(GameObjectInfo ObjectInfo, MovementState Movement, bool IgnoreClosedDoors, Bot? Bot, Monster? Monster, AreaType FromArea, MovementRequest Request)
    {
        public MovementResult Result { get; set; }
    }

    internal readonly record struct MovementResult(bool Changed, bool ReachedPathEnd);
}
