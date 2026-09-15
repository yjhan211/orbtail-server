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
            PrepareMovement(runtime, target.ObjectInfo, target.Intent, target.Request, nowUtc, target.IgnoreClosedDoors);
        }

        // 이동
        foreach (var target in movementTargets)
        {
            target.Result = Move(runtime, target.ObjectInfo, target.Intent, target.Request, target.IgnoreClosedDoors, nowUtc);
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
        movement.ResetIntent();
        if (runtime.IsEnded)
        {
            return;
        }
        if (request.HoldPosition || request.Speed <= 0f)
        {
            if (movement.WaypointIndex < movement.Waypoints.Count && !CanKeepPath(runtime, objectInfo, movement, nowUtc, ignoreClosedDoors, request.IsSafeCell))
            {
                movement.Waypoints.Clear();
                movement.WaypointIndex = 0;
                movement.NextPathPlanAtUtc = DateTime.MinValue;
            }
            return;
        }

        var destination = request.DestinationCell;
        if (destination == null && movement.Waypoints.Count > 0)
        {
            destination = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, movement.Waypoints[^1]);
        }
        destination ??= movement.DestinationCell;
        if (destination == null)
        {
            return;
        }
        bool firstRequest = movement.DestinationCell == null;
        bool changed = firstRequest ||
            !movement.DestinationCell!.Equals(destination);
        if (changed)
        {
            movement.ReachedDestination = false;
        }
        movement.DestinationCell = destination.Clone();
        movement.DestinationArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, destination);
        bool hasPath = movement.WaypointIndex < movement.Waypoints.Count;

        if (!hasPath && movement.ReachedDestination && !changed && objectInfo.Cell.Equals(destination))
        {
            return;
        }
        bool needsPlan = (request.IsSafeCell != null && changed) || (firstRequest && !hasPath) || movement.PathBlocked || nowUtc >= movement.NextPathPlanAtUtc;
        // 공급 등 이미 주어진 경로를 이어 가라는 요청은 완료 또는 차단 전까지 유지한다.
        if (request.DestinationCell == null && hasPath && !movement.PathBlocked)
        {
            needsPlan = false;
        }
        if (needsPlan)
        {
            bool planned = TryPlanPath(runtime, objectInfo, movement,
                movement.DestinationArea, destination, ignoreClosedDoors, request.IsSafeCell);
            if (!planned && request.FallbackCell is { } fallback)
            {
                var fallbackArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, fallback);
                planned = TryPlanPath(runtime, objectInfo, movement, fallbackArea, fallback, ignoreClosedDoors, request.IsSafeCell);
            }
            movement.NextPathPlanAtUtc = nowUtc.AddSeconds(Config.SWARM_MONSTER_CHASE_PLAN_INTERVAL_SECONDS);
            movement.PathBlocked = false;
            if (!planned && !CanKeepPath(runtime, objectInfo, movement, nowUtc, ignoreClosedDoors, request.IsSafeCell))
            {
                movement.Waypoints.Clear();
                movement.WaypointIndex = 0;
            }
        }
        if (request.IsSafeCell != null && !CanKeepPath(runtime, objectInfo, movement, nowUtc, ignoreClosedDoors, request.IsSafeCell))
        {
            movement.Waypoints.Clear();
            movement.WaypointIndex = 0;
        }
        movement.FollowPath = movement.WaypointIndex < movement.Waypoints.Count;
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

    private sealed record MovementTarget(GameObjectInfo ObjectInfo, MovementState Intent, bool IgnoreClosedDoors, Bot? Bot, Monster? Monster, AreaType FromArea, MovementRequest Request)
    {
        public MovementResult Result { get; set; }
    }

    internal readonly record struct MovementResult(bool Changed, bool ReachedDestination);

    internal static MovementResult Move(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState intent, MovementRequest request, bool ignoreClosedDoors = false, DateTime? nowUtc = null)
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
            // 시작 전 대기 시간은 첫 이동량에 포함하지 않는다.
            var lastProcessedAtUtc = intent.LastProcessedAtUtc;
            if (runtime.StartsAtUtc is { } startedAtUtc && lastProcessedAtUtc < startedAtUtc)
            {
                lastProcessedAtUtc = startedAtUtc;
            }
            double elapsedSeconds = (now - lastProcessedAtUtc).TotalSeconds;
            float deltaSeconds = (float)Math.Clamp(elapsedSeconds, 0d, MaxMovementDeltaSeconds);
            intent.LastProcessedAtUtc = now;
            Vector3f? next = null;
            bool pathBlocked = false;
            bool reachedDestination = false;
            float distance = intent.FollowPath && !request.HoldPosition ? request.Speed * deltaSeconds : 0f;
            if (next == null && distance > 0f && intent.FollowPath)
            {
                var path = intent;
                next = AdvanceRoute(runtime, path, previous, distance, ignoreClosedDoors, now, request.StopBeforeArea);
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
            intent.PathBlocked = pathBlocked && !reachedDestination;
            intent.ReachedDestination |= reachedDestination;
            if (reachedDestination)
            {
                intent.Waypoints.Clear();
                intent.WaypointIndex = 0;
            }
            return new MovementResult(changed, reachedDestination);
        }
        finally
        {
            intent.ResetIntent();
        }
    }

    internal static Vector3f AdvanceRoute(MatchRuntime runtime, MovementState movement, Vector3f position, float distanceBudget, bool ignoreClosedDoors = false, DateTime? nowUtc = null, AreaType? stopBeforeArea = null)
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
            if ((stopBeforeArea.HasValue && targetArea == stopBeforeArea.Value) ||
                IsUnsafeStep(runtime, position, GameMapData.GetCurrentArea(mapId,
                    MapCoordinateConverter.WorldToCell(mapId, position)), targetCell, targetArea, now))
            {
                break;
            }
            if (!MapTraversal.IsTraversable(mapId, current, target))
            {
                break;
            }

            float step = Math.Min(distanceBudget, distance);
            var next = Vector3f.MoveTowardsXY(current, target, step);
            var nextCell = MapCoordinateConverter.WorldToCell(mapId, next);
            var nextArea = GameMapData.GetCurrentArea(mapId, nextCell);
            if ((stopBeforeArea.HasValue && nextArea == stopBeforeArea.Value) ||
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

    private static bool CanKeepPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement, DateTime nowUtc, bool ignoreClosedDoors, Func<Cell, bool>? isSafeCell = null)
    {
        if (movement.WaypointIndex >= movement.Waypoints.Count)
        {
            return false;
        }
        var previous = objectInfo.Cell;
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        for (int i = movement.WaypointIndex; i < movement.Waypoints.Count; i++)
        {
            var next = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, movement.Waypoints[i]);
            bool valid = MapTraversal.IsTraversable(previous, next,
                cell => (isSafeCell == null || isSafeCell(cell)) && GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) &&
                    !runtime.Closures.IsAreaClosed(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell)) &&
                    SwarmPressureField.GetDistance(cell) <= safeDistance,
                (from, to) => runtime.Doors.GetBlockingDoor(
                    GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, from),
                    GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, to), from, to, ignoreClosedDoors) == null);
            if (!valid)
            {
                return false;
            }
            previous = next;
        }
        return true;
    }

    internal static bool TryPlanPath(MatchRuntime runtime, GameObjectInfo objectInfo, MovementState movement,
        AreaType destinationArea, Cell destinationCell, bool ignoreClosedDoors = false, Func<Cell, bool>? isSafeCell = null)
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

        // 닫힌 문을 건너기 전에는 해당 구역의 등록된 상호작용 셀에 먼저 도착한다.
        var previousCell = objectInfo.Cell;
        var previousArea = objectInfo.Area;
        foreach (var step in path)
        {
            var nextArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, step.Cell);
            var door = ignoreClosedDoors ? null : runtime.Doors.GetBlockingDoor(previousArea, nextArea, previousCell, step.Cell);
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
                if (interaction == null) return false;
                path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, objectInfo.Area, objectInfo.Cell,
                    (AreaType)interaction.ZoneId, new Cell(interaction.CellX, interaction.CellY));
                if (path == null || path.Count == 0) return false;
                break;
            }
            previousCell = step.Cell;
            previousArea = nextArea;
        }
        var planned = new List<Vector3f>(path.Count);
        var previousPosition = objectInfo.Position;
        foreach (var step in path)
        {
            if (isSafeCell != null && !isSafeCell(step.Cell))
            {
                return false;
            }
            var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, step.Cell);
            if (!MapPathfinder.IsSegmentWalkable(Config.SWARM_MATCH_MAP, previousPosition, position))
            {
                return false;
            }
            planned.Add(position);
            previousPosition = position;
        }
        movement.Waypoints.Clear();
        movement.Waypoints.AddRange(planned);
        movement.WaypointIndex = 0;
        return true;
    }

    // 행동 서비스가 정렬한 후보 중 실제로 도달 가능한 첫 셀을 선택한다.
    // 목적지만 안전한 경로가 아니라 모든 경유 셀이 행동의 안전 조건을 만족해야 한다.
    internal static bool TrySelectReachableCell(
        MatchRuntime runtime, GameObjectInfo objectInfo, IReadOnlyList<Cell> candidates,
        Func<Cell, bool> isSafeCell, Func<AreaType, bool> isBlockedArea, out Cell targetCell)
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
}
