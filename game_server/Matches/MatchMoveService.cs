using game_server.players.bots;
using game_server.matches.monsters;
using MessagePack;
using network.packets;
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

        var movements = new List<(GameObjectInfo Info, AreaType FromArea)>();
        foreach (var target in movementTargets)
        {
            if (target.Bot is { } bot)
            {
                if (!target.Result.Changed)
                {
                    continue;
                }
                bot.Player.AdvanceOrbOrbit(target.ObjectInfo.Position);
            }
            movements.Add((target.ObjectInfo, target.FromArea));
        }

        // 이동 후 결과 브로드캐스트
        CompleteMovements(runtime, movements);
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

    internal void CompleteMovements(MatchRuntime runtime, IReadOnlyList<(GameObjectInfo Info, AreaType FromArea)> movements)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Object movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish object movement after the match has ended.");
        }

        var sessions = runtime.GetSessions();
        var snapshots = runtime.Monsters.GetVisualStatesByArea();
        foreach (var session in sessions)
        {
            session.SendMonsterSnapshot(snapshots, preMatch: false);
        }

        foreach (var movement in movements)
        {
            var info = movement.Info;
            if (info.ObjectType != ObjectType.PLAYER)
            {
                continue;
            }

            var player = runtime.Bots.GetBot(info.ObjectId)?.Player;
            if (player != null && player.State == PlayerState.EXPLORE_1 && (info.Velocity.X != 0f || info.Velocity.Y != 0f))
            {
                player.ClearPendingInteractions();
                player.State = PlayerState.IDLE;
                using var statePacket = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == movement.FromArea || session.Player.CurrentArea == info.Area)
                    {
                        session.TrySend(statePacket);
                    }
                }
            }

            if (movement.FromArea != info.Area)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(info.ObjectId);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == movement.FromArea)
                    {
                        session.TrySend(leavePacket);
                    }
                }

                var enteringBot = runtime.Bots.GetPlayerObjectInfo(info.ObjectId);
                if (enteringBot != null)
                {
                    using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(enteringBot);
                    foreach (var session in sessions)
                    {
                        if (session.Player.CurrentArea == info.Area)
                        {
                            session.TrySend(enterPacket);
                        }
                    }
                }
            }
        }

        foreach (var session in sessions)
        {
            var message = new G_TO_C_MOVE
            {
                ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            foreach (var movement in movements)
            {
                var info = movement.Info;
                bool inArea = session.Player.CurrentArea == info.Area;
                bool departingMonster = info.ObjectType == ObjectType.MONSTER && session.Player.CurrentArea == movement.FromArea;
                if (!inArea && !departingMonster)
                {
                    continue;
                }
                message.Objects.Add(info.Clone());
                if (info.ObjectType == ObjectType.PLAYER)
                {
                    var player = runtime.Bots.GetBot(info.ObjectId)?.Player;
                    message.OrbPhases[info.ObjectId] = player?.OrbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(info.ObjectId);
                }
            }

            if (message.Objects.Count == 0)
            {
                continue;
            }
            using var packet = Packet.Create((int)Protocol.G_TO_C_MOVE);
            packet.SetBody(MessagePackSerializer.Serialize(message));
            session.TrySend(packet);
        }
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

    // 행동 서비스가 정렬한 후보 중 실제로 도달 가능한 첫 셀을 선택한다.
    // 목적지만 안전한 경로가 아니라 모든 경유 셀이 행동의 안전 조건을 만족해야 한다.
    internal static bool TrySelectReachableCell(MatchRuntime runtime, GameObjectInfo objectInfo, IReadOnlyList<Cell> candidates, Func<Cell, bool> isSafeCell, Func<AreaType, bool> isBlockedArea, out Cell targetCell)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement target selection requires the match lock.");
        }
        targetCell = null!;
        if (runtime.IsEnded)
        {
            return false;
        }
        foreach (var candidate in candidates)
        {
            var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, candidate);
            if (area == AreaType.None || isBlockedArea(area) || !isSafeCell(candidate))
            {
                continue;
            }
            var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP,
                objectInfo.Area, objectInfo.Cell, area, candidate, isBlockedArea);
            if (path is not { Count: > 0 })
            {
                continue;
            }
            bool safe = true;
            foreach (var step in path)
            {
                if (!isSafeCell(step.Cell))
                {
                    safe = false;
                    break;
                }
            }
            if (!safe)
            {
                continue;
            }
            targetCell = candidate;
            return true;
        }
        return false;
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

    private sealed record MovementTarget(GameObjectInfo ObjectInfo, MovementState Movement, bool IgnoreClosedDoors, Bot? Bot, Monster? Monster, AreaType FromArea, MovementRequest Request)
    {
        public MovementResult Result { get; set; }
    }

    internal readonly record struct MovementResult(bool Changed, bool ReachedPathEnd);
}
