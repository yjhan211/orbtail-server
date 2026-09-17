using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
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
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation || !runtime.IsGameplayActive(nowUtc))
        {
            return;
        }

        var movementActors = new List<MovementActor>();
        var participants = runtime.GetAlivePlayers().FindAll(player => player.Position != null);

        // 이동 타겟 선정
        foreach (var bot in runtime.Bots.GetBots())
        {
            if (bot.Player.IsEliminated)
            {
                continue;
            }
            var request = botBehavior.CreateMovementRequest(runtime, bot, nowUtc);
            movementActors.Add(new MovementActor(bot.Player.GameInfo.ObjectInfo, bot.Movement, false, request, bot.Player));
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
            actor.Result = Move(runtime, actor.ObjectInfo, actor.Movement, actor.Request, nowUtc);
        }

        // 자리를 옮긴 봇은 사람의 이동 패킷과 같은 후처리
        foreach (var actor in movementActors)
        {
            if (actor.Result.Changed && actor.Player != null)
            {
                PlayerMovementService.CancelDoorOpeningIfMoved(runtime, actor.Player);
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
            movement.ClearAndReplan();
            return;
        }

        var destination = request.DestinationCell;
        if (destination == null)
        {
            return;
        }

        // 가던 길이 문이나 벽으로 막혔으면 버리고 바로 다시 계획
        if (movement.WaypointIndex < movement.Waypoints.Count && !CanKeepPath(runtime, objectInfo, movement, ignoreClosedDoors))
        {
            movement.ClearAndReplan();
        }

        // 재탐색 시점 전에는 기존 경로를 유지
        if (nowUtc < movement.NextPathPlanAtUtc)
        {
            return;
        }

        // 경로 탐색을 매 틱 수행하지 않도록 다음 탐색 가능 시간 지정
        movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
        var currentArea = GameMapData.GetCurrentArea(objectInfo.MapId, objectInfo.Cell);
        var destinationArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, destination);
        var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, currentArea, objectInfo.Cell, destinationArea, destination);
        if (path == null || path.Count == 0)
        {
            return;
        }

        var previousCell = objectInfo.Cell;
        var previousArea = currentArea;

        // 계산된 경로를 순회하며 닫힌 문이 있다면 문으로 목표 변경
        foreach (var step in path)
        {
            var nextArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, step.Cell);
            var door = runtime.Doors.GetBlockingDoor(previousArea, nextArea, previousCell, step.Cell, ignoreClosedDoors);
            if (door != null)
            {
                InteractableInfoData? interaction = null;
                foreach (var info in GameInteractableData.GetAll())
                {
                    if (info.DoorId == door.DoorId && info.ZoneId == (int)previousArea)
                    {
                        interaction = info;
                        break;
                    }
                }
                if (interaction == null)
                {
                    return;
                }
                path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, currentArea, objectInfo.Cell, (AreaType)interaction.ZoneId, new Cell(interaction.CellX, interaction.CellY));
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

        movement.Clear();
        foreach (var step in path)
        {
            movement.Waypoints.Add(step.Cell.Clone());
        }
    }

    internal static MovementResult Move(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, MovementRequest request, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }

        // 게임 시작 전에 쌓인 시간은 이동 거리로 치지 않음
        var previous = objectInfo.Position;
        var lastProcessedAtUtc = movement.LastProcessedAtUtc;
        if (runtime.StartsAtUtc is { } startedAtUtc && lastProcessedAtUtc < startedAtUtc)
        {
            lastProcessedAtUtc = startedAtUtc;
        }

        double elapsedSeconds = (nowUtc - lastProcessedAtUtc).TotalSeconds;
        float deltaSeconds = (float)Math.Clamp(elapsedSeconds, 0d, MaxMovementDeltaSeconds);
        movement.LastProcessedAtUtc = nowUtc;

        var next = previous;
        bool reachedPathEnd = false;
        // 속도가 0 이하면 정지하고 기존 경로는 유지한
        float distance = Math.Max(0f, request.Speed) * deltaSeconds;
        if (distance > 0f && movement.WaypointIndex < movement.Waypoints.Count)
        {
            next = MoveAlongPath(runtime, movement, previous, distance);
            reachedPathEnd = movement.WaypointIndex >= movement.Waypoints.Count;
            if (!reachedPathEnd && next.Equals(previous))
            {
                // 경로가 남았는데 한 발도 못 움직였으면 막힌 것이므로 다시 계획
                movement.ClearAndReplan();
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
            movement.Clear();
        }
        return new MovementResult(changed, reachedPathEnd);
    }

    internal static Vector3f MoveAlongPath(MatchRuntime runtime, MovementState movement, Vector3f position, float remainingDistance)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }

        int index = movement.WaypointIndex;
        var current = position;
        while (remainingDistance > 0f && index < movement.Waypoints.Count)
        {
            var target = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, movement.Waypoints[index]);
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distanceToWaypoint = MathF.Sqrt(dx * dx + dy * dy);
            if (remainingDistance >= distanceToWaypoint)
            {
                // 웨이포인트에 닿으면 오차가 쌓이지 않게 좌표를 그대로 씀
                current = new Vector3f(target.X, target.Y, 0f);
                remainingDistance -= distanceToWaypoint;
                index++;
                continue;
            }

            float ratio = remainingDistance / distanceToWaypoint;
            current = new Vector3f(current.X + dx * ratio, current.Y + dy * ratio, 0f);
            remainingDistance = 0f;
        }
        movement.WaypointIndex = index;
        return current;
    }

    private static bool CanKeepPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, bool ignoreClosedDoors)
    {
        var previous = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, objectInfo.Position);
        for (int i = movement.WaypointIndex; i < movement.Waypoints.Count; i++)
        {
            var next = movement.Waypoints[i];
            if (!CanTraverse(runtime, previous, next, ignoreClosedDoors))
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
}
