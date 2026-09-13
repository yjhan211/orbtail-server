using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players.bots;

/// <summary>
///     전달받은 매치의 봇 경로·걷기·회피를 처리하고, 구역별 수신자에게 이동 결과를 전송한다.
///     호출자는 매치 잠금을 보유한다. 다른 매치 조회나 타이머 관리는 하지 않는다.
///     봇의 전술 판단은 전달받은 함수에 위임한다.
/// </summary>
internal class BotMovementService(ILogger<BotMovementService> logger)
{
    public virtual void ProcessTick(MatchRuntime runtime, Action<long> decideMovement)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot movement requires the match lock.");
        }

        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        }
        ArgumentNullException.ThrowIfNull(decideMovement);
        var sessions = runtime.GetSessions();
        var playerAreas = new Dictionary<long, AreaType>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerAreas.Add(player.PlayerId, player.CurrentArea);
        }
        var result = ProcessBotMovementTick(runtime, playerAreas, decideMovement);

        foreach (var movement in result.Movements)
        {
            runtime.Bots.GetBot(movement.BotPlayerId)?.Player.AdvanceOrbOrbit(movement.Position);
        }
        foreach (var movement in result.Movements)
        {
            SendMovement(runtime, movement, sessions);
        }
    }

    private void SendMovement(MatchRuntime runtime, BotMovementResult movement, IReadOnlyList<GameClientSession> sessions)
    {
        if (movement.IsAreaTransition)
        {
            using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(movement.BotPlayerId);
            foreach (var session in sessions)
            {
                if (session.PlayerId is > 0 && session.MatchingId == runtime.MatchingId && session.Player.CurrentArea == movement.FromArea)
                {
                    session.TrySend(leavePacket);
                }
            }

            var enteringBot = runtime.Bots.GetGameObjectInfo(movement.BotPlayerId);
            if (enteringBot != null)
            {
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(enteringBot);
                foreach (var session in sessions)
                {
                    if (session.PlayerId is > 0 && session.MatchingId == runtime.MatchingId &&
                        session.Player.CurrentArea == movement.ToArea)
                    {
                        session.TrySend(enterPacket);
                    }
                }
            }
        }

        float orbOrbitPhase = runtime.Bots.GetBot(movement.BotPlayerId)?.Player.OrbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
        using var movePacket = PacketMaker.G_TO_C_MOVE(movement.BotPlayerId, movement.Position, movement.Velocity, movement.Rotation, movement.ToCell, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), orbOrbitPhase);
        foreach (var session in sessions)
        {
            if (session.PlayerId is > 0 && session.MatchingId == runtime.MatchingId && session.Player.CurrentArea == movement.ToArea)
            {
                session.TrySend(movePacket);
            }
        }
    }

    public void DispatchExternalMovement(MatchRuntime runtime, BotMovementResult movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        }
        SendMovement(runtime, movement, runtime.GetSessions());
    }

    private static float DistanceSquared(Vector3f position, float x, float y)
    {
        float dx = position.X - x;
        float dy = position.Y - y;
        return dx * dx + dy * dy;
    }

    private static float ScaledWalkSpeed(float dirX, float dirY, float movementMultiplier = 1f)
    {
        float tileFactor = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
        float baseSpeed = Config.SWARM_BOT_WALK_SPEED * Math.Max(0f, movementMultiplier);
        return tileFactor > 0.0001f ? baseSpeed / tileFactor : baseSpeed;
    }

    internal static float GetBotMovementSpeedMultiplier(Bot bot)
    {
        float wind = OrbData.GetWindMoveSpeedMultiplier(bot.Player.Orbs.GetAllItems());
        float boots = DateTime.UtcNow < bot.BootsSpeedUntilUtc ? Config.BOOTS_MOVE_SPEED_MULTIPLIER : 1f;
        float bare = bot.IsSwarmBareHanded && DateTime.UtcNow < bot.SwarmBareSpeedUntilUtc ? Config.SWARM_BARE_MOVE_SPEED_MULTIPLIER : 1f;
        float waveSlow = DateTime.UtcNow < bot.Player.WaveSlowUntilUtc ? OrbData.WaveSlowMoveSpeedMultiplier : 1f;
        return wind * boots * bare * waveSlow;
    }
    private static Vector3f ScaledWalkVelocity(float dirX, float dirY, float movementMultiplier = 1f)
    {
        float speed = ScaledWalkSpeed(dirX, dirY, movementMultiplier);
        return new Vector3f(dirX * speed, dirY * speed, 0f);
    }

    public class BotWalkingTickResult
    {
        public List<BotMovementResult> Movements { get; } = new();
        public long PlanningBotId { get; set; }
    }

    public BotWalkingTickResult ProcessBotMovementTick(MatchRuntime runtime, IReadOnlyDictionary<long, AreaType> playerAreas, Action<long> decideMovement)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot movement requires the match lock.");
        }

        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        }
        var result = new BotWalkingTickResult();
        var bots = runtime.Bots.GetBots();
        var activeBots = new List<Bot>();
        foreach (var bot in bots)
        {
            if (!bot.Player.IsEliminated)
            {
                activeBots.Add(bot);
            }
        }
        if (activeBots.Count == 0)
        {
            return result;
        }

        result.PlanningBotId = runtime.Bots.SelectMovementPlanningBot(activeBots);
        // 판단은 모든 봇이 하되, 이 경로 갱신은 틱마다 한 봇씩 차례를 나눈다.
        var nowUtc = DateTime.UtcNow;
        foreach (var bot in activeBots)
        {
            if (bot.Player.IsSleeping)
            {
                bot.LastWalkStepTime = nowUtc;
                var stopped = StopMovement(bot);
                if (stopped != null)
                {
                    result.Movements.Add(stopped);
                }
                continue;
            }

            decideMovement(bot.PlayerId);
            if (bot.DesiredMovementMode == BotMovementMode.None)
            {
                bot.MovementMode = BotMovementMode.None;
                bot.ClearPath();
                bot.LastWalkStepTime = nowUtc;
                var stopped = StopMovement(bot);
                if (stopped != null)
                {
                    result.Movements.Add(stopped);
                }
                continue;
            }

            bool canPlanThisTick = bot.PlayerId == result.PlanningBotId;
            bool underFire = (nowUtc - bot.LastDamagedAtUtc).TotalSeconds <= 6d;
            if (canPlanThisTick && (bot.MovementMode == BotMovementMode.None || nowUtc >= bot.MovementModeUntilUtc || underFire))
            {
                bool changed = bot.MovementMode != bot.DesiredMovementMode;
                bot.MovementMode = bot.DesiredMovementMode;
                const double holdSeconds = 1.5;
                if (changed || bot.MovementModeUntilUtc <= nowUtc)
                {
                    bot.MovementModeUntilUtc = nowUtc.AddSeconds(holdSeconds);
                }
                bot.MovementDestination = bot.DesiredMovementArea;
                bot.SetPath(MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, bot.Player.CurrentArea, bot.Player.Cell!, bot.DesiredMovementArea, bot.DesiredMovementCell) ?? []);
            }

            TrackIdleTime(bot, nowUtc);
            if (bot.PathIndex >= bot.Path.Count)
            {
                TryStartIdleWander(runtime, bot, nowUtc);
                if (bot.PathIndex >= bot.Path.Count)
                {
                    bot.LastWalkStepTime = nowUtc;
                    var stopped = StopMovement(bot);
                    if (stopped != null)
                    {
                        result.Movements.Add(stopped);
                    }
                    continue;
                }
            }

            var movement = WalkStep(runtime, bot, playerAreas, canPlanThisTick);
            if (movement != null)
            {
                result.Movements.Add(movement);
            }
        }

        return result;
    }

    private void TrackIdleTime(Bot bot, DateTime nowUtc)
    {
        const float movedThresholdSquared = 0.01f;
        if (bot.IdleWatchLastPosition == null || DistanceSquared(bot.IdleWatchLastPosition, bot.Player.Position!.X, bot.Player.Position!.Y) > movedThresholdSquared)
        {
            bot.IdleWatchLastPosition = new Vector3f(bot.Player.Position!.X, bot.Player.Position!.Y, 0f);
            bot.IdleWatchLastMovedAtUtc = nowUtc;
            return;
        }

        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 6d || (nowUtc - bot.IdleWatchLastLoggedAtUtc).TotalSeconds < 10d)
        {
            return;
        }

        bot.IdleWatchLastLoggedAtUtc = nowUtc;
        logger.LogInformation(
            "Swarm bot idle: BotId={BotId}, Area={Area}, IdleSeconds={IdleSeconds:F0}, " +
            "Mode={Mode}, DirectiveArea={DirectiveArea}, PathRemaining={PathRemaining}",
            bot.PlayerId,
            bot.Player.CurrentArea,
            (nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds,
            bot.DesiredMovementMode,
            bot.DesiredMovementArea,
            Math.Max(0, bot.Path.Count - bot.PathIndex));
    }

    private void TryStartIdleWander(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 4d || nowUtc < bot.NextIdleWanderAtUtc)
        {
            return;
        }

        bot.NextIdleWanderAtUtc = nowUtc.AddSeconds(3d);
        var mapId = Config.SWARM_MATCH_MAP;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var candidate = new Cell(
                bot.Player.Cell!.X + Random.Shared.Next(-3, 4),
                bot.Player.Cell!.Y + Random.Shared.Next(-3, 4));
            if (candidate.X == bot.Player.Cell!.X && candidate.Y == bot.Player.Cell!.Y)
                continue;
            if (!GameMapData.IsMoveablePosition(mapId, candidate) ||
                GameMapData.GetCurrentArea(mapId, candidate) != bot.Player.CurrentArea)
                continue;

            var path = MapPathfinder.FindPath(mapId, bot.Player.CurrentArea, bot.Player.Cell!, bot.Player.CurrentArea, candidate);
            if (path == null || path.Count == 0)
                continue;

            bot.SetPath(path);
            return;
        }
    }

    private int CountAreaPressure(MatchRuntime runtime, AreaType area, long excludeBotPlayerId = 0)
    {
        return runtime.Bots.GetBots().Count(other =>
            {
                if (other.Player.IsEliminated || (excludeBotPlayerId != 0 && other.PlayerId == excludeBotPlayerId))
                {
                    return false;
                }

                var committedArea = other.EvacuationDestination != AreaType.None
                    ? other.EvacuationDestination
                    : other.MovementDestination != AreaType.None
                        ? other.MovementDestination
                        : other.Player.CurrentArea;
                return committedArea == area;
            });
    }

    private static bool IsClosedArea(MatchRuntime runtime, AreaType area)
    {
        if (area == AreaType.None)
        {
            return false;
        }

        return runtime.Closures.IsAreaClosed(area);
    }

    private static BotMovementResult? StopMovement(Bot bot)
    {
        if (bot.Player.Velocity.X == 0f && bot.Player.Velocity.Y == 0f)
        {
            return null;
        }
        bot.Player.Velocity = new Vector3f();
        return new BotMovementResult
        {
            BotPlayerId = bot.PlayerId,
            FromArea = bot.Player.CurrentArea,
            ToArea = bot.Player.CurrentArea,
            ToCell = bot.Player.Cell!,
            Position = bot.Player.Position!,
            Velocity = bot.Player.Velocity,
            Rotation = bot.Player.Rotation,
            IsAreaTransition = false
        };
    }

    private BotMovementResult? WalkStep(MatchRuntime runtime, Bot bot, IReadOnlyDictionary<long, AreaType> playerAreas, bool allowPathPlanning)
    {
        var now = DateTime.UtcNow;
        float deltaSec = (float)(now - bot.LastWalkStepTime).TotalSeconds;
        if (deltaSec <= 0) deltaSec = 0.25f;
        bot.LastWalkStepTime = now;
        if (TryDodgeStep(runtime, bot, now, deltaSec, out var dodgeMovement))
        {
            return dodgeMovement;
        }

        if (now < bot.LoopWaitUntil)
        {
            return StopMovement(bot);
        }

        if (bot.Path.Count == 0 || bot.PathIndex >= bot.Path.Count)
        {
            if (!allowPathPlanning)
            {
                return StopMovement(bot);
            }
            ChooseNewWanderTarget(runtime, bot, playerAreas);
            if (bot.Path.Count == 0)
            {
                return StopMovement(bot);
            }
            if (now < bot.LoopWaitUntil)
            {
                return StopMovement(bot);
            }
        }

        var nextStep = bot.Path[bot.PathIndex];
        var mapId = Config.SWARM_MATCH_MAP;
        var fromArea = bot.Player.CurrentArea;
        if (IsUnsafeStep(runtime, bot, nextStep.Cell, nextStep.Area, now))
        {
            bot.ClearPath();
            bot.MovementDestination = AreaType.None;
            bot.EvacuationDestination = AreaType.None;
            bot.MovementModeUntilUtc = DateTime.MinValue;
            bot.LoopWaitUntil = DateTime.MinValue;
            return StopMovement(bot);
        }
        bool reachedStep = false;
        if (nextStep.Area != bot.Player.CurrentArea)
        {
            var transitionDoor = GameDoorData.GetDoorForTransition(bot.Player.CurrentArea, nextStep.Area, bot.Player.Cell!, nextStep.Cell);
            if (transitionDoor != null && !runtime.Doors.IsDoorOpen(transitionDoor.DoorId))
            {
                bot.ClearPath();
                bot.MovementDestination = AreaType.None;
                bot.EvacuationDestination = AreaType.None;
                bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.4);
                if (bot.LastLockedDoorBlockArea != nextStep.Area)
                {
                    bot.LastLockedDoorBlockArea = nextStep.Area;
                    logger.LogInformation("Bot blocked at closed door: MatchingId={MatchingId}, BotId={BotId}, " + "From={From}, To={To}, DoorId={DoorId}", runtime.MatchingId, bot.PlayerId, bot.Player.CurrentArea, nextStep.Area, transitionDoor.DoorId);
                }
                return StopMovement(bot);
            }
        }

        var targetPos = MapCoordinateConverter.CellToWorld(mapId, nextStep.Cell);
        float dx = targetPos.X - bot.Player.Position!.X;
        float dy = targetPos.Y - bot.Player.Position!.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float maxDist = Config.SWARM_BOT_WALK_SPEED * GetBotMovementSpeedMultiplier(bot) * deltaSec;
        if (dist >= 0.01f)
        {
            maxDist = ScaledWalkSpeed(dx / dist, dy / dist, GetBotMovementSpeedMultiplier(bot)) * deltaSec;
        }

        Vector3f newPosition;
        Vector3f velocity;
        if (!GameMapData.IsMoveablePosition(mapId, nextStep.Cell) && GameMapData.IsMoveablePosition(mapId, bot.Player.Cell!))
        {
            bot.ClearPath();
            bot.MovementDestination = AreaType.None;
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.4, 0.9);
            return StopMovement(bot);
        }

        if (dist <= maxDist || dist < 0.01f)
        {
            newPosition = targetPos;
            bot.Player.Cell = nextStep.Cell;
            bot.Player.Position = newPosition;
            bot.PathIndex++;
            reachedStep = true;
            velocity = new Vector3f(0f, 0f, 0f);
            // 구역 경계는 다음 틱의 문·위험 검사 후 통과한다.
            if (bot.PathIndex < bot.Path.Count && bot.Path[bot.PathIndex].Area == fromArea &&
                !IsUnsafeStep(runtime, bot, bot.Path[bot.PathIndex].Cell, bot.Path[bot.PathIndex].Area, now))
            {
                var followingPos = MapCoordinateConverter.CellToWorld(mapId, bot.Path[bot.PathIndex].Cell);
                float nextDx = followingPos.X - newPosition.X;
                float nextDy = followingPos.Y - newPosition.Y;
                float nextDist = (float)Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextDist > 0.01f)
                {
                    velocity = ScaledWalkVelocity(nextDx / nextDist, nextDy / nextDist, GetBotMovementSpeedMultiplier(bot));

                    float leftover = maxDist - dist;
                    if (leftover > 0f)
                    {
                        float carry = Math.Min(leftover, nextDist);
                        newPosition = new Vector3f(newPosition.X + nextDx / nextDist * carry, newPosition.Y + nextDy / nextDist * carry, 0f);
                        bot.Player.Position = newPosition;
                    }
                }
            }
        }
        else
        {
            float dirX = dx / dist;
            float dirY = dy / dist;
            newPosition = new Vector3f(bot.Player.Position!.X + dirX * maxDist, bot.Player.Position!.Y + dirY * maxDist, 0f);
            velocity = ScaledWalkVelocity(dirX, dirY, GetBotMovementSpeedMultiplier(bot));
            bot.Player.Position = newPosition;
        }

        bool areaChanged = false;
        if (reachedStep)
        {
            var resolvedArea = GameMapData.GetCurrentArea(mapId, bot.Player.Cell!);
            if (resolvedArea != AreaType.None && resolvedArea != bot.Player.CurrentArea)
            {
                bot.Player.CurrentArea = resolvedArea;
                areaChanged = true;
            }
        }

        bot.Player.Velocity = velocity;
        if (velocity.X > 0.1f)
        {
            bot.Player.Rotation = 180f;
        }
        else if (velocity.X < -0.1f)
        {
            bot.Player.Rotation = 0f;
        }

        return new BotMovementResult
        {
            BotPlayerId = bot.PlayerId,
            FromArea = fromArea,
            ToArea = bot.Player.CurrentArea,
            ToCell = nextStep.Cell,
            Position = newPosition,
            Velocity = velocity,
            Rotation = bot.Player.Rotation,
            IsAreaTransition = areaChanged
        };
    }

    private bool TryDodgeStep(MatchRuntime runtime, Bot bot, DateTime now, float deltaSec, out BotMovementResult? movement)
    {
        movement = null;
        if (bot.Player.CurrentArea == AreaType.None)
        {
            return false;
        }

        bool committed = now < bot.SwarmDodgeHoldUntilUtc;
        var advice = BotDodgeCalculator.CalculateDodge(runtime.SunCrossfireShapes, bot.PlayerId, bot.Player.Position!, bot.Player.CurrentArea, now);
        if (advice == null)
        {
            if (!committed)
            {
                return false;
            }
            if (bot.Player.Velocity.X != 0f || bot.Player.Velocity.Y != 0f)
            {
                bot.Player.Velocity = new Vector3f(0f, 0f, 0f);
                movement = new BotMovementResult
                {
                    BotPlayerId = bot.PlayerId,
                    FromArea = bot.Player.CurrentArea,
                    ToArea = bot.Player.CurrentArea,
                    ToCell = bot.Player.Cell!,
                    Position = bot.Player.Position!,
                    Velocity = bot.Player.Velocity,
                    Rotation = bot.Player.Rotation,
                    IsAreaTransition = false
                };
            }
            return true;
        }

        float adviceX = advice.Value.DirectionX;
        float adviceY = advice.Value.DirectionY;
        float dirX0 = adviceX;
        float dirY0 = adviceY;
        if (committed && bot.SwarmDodgeDirectionX * adviceX + bot.SwarmDodgeDirectionY * adviceY > 0f)
        {
            dirX0 = bot.SwarmDodgeDirectionX;
            dirY0 = bot.SwarmDodgeDirectionY;
        }
        else
        {
            bot.SwarmDodgeDirectionX = adviceX;
            bot.SwarmDodgeDirectionY = adviceY;
        }
        var holdUntil = now.AddSeconds(advice.Value.HoldSeconds);
        if (holdUntil > bot.SwarmDodgeHoldUntilUtc)
        {
            bot.SwarmDodgeHoldUntilUtc = holdUntil;
        }

        var mapId = Config.SWARM_MATCH_MAP;
        float multiplier = GetBotMovementSpeedMultiplier(bot);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            float sign = attempt == 0 ? 1f : -1f;
            float dirX = dirX0 * sign;
            float dirY = dirY0 * sign;
            if (attempt == 1)
            {
                bot.SwarmDodgeDirectionX = dirX;
                bot.SwarmDodgeDirectionY = dirY;
            }
            float step = ScaledWalkSpeed(dirX, dirY, multiplier) * deltaSec;
            var candidate = new Vector3f(bot.Player.Position!.X + dirX * step, bot.Player.Position!.Y + dirY * step, 0f);
            var candidateCell = MapCoordinateConverter.WorldToCell(mapId, candidate);
            if (!GameMapData.IsMoveablePosition(mapId, candidateCell) || GameMapData.GetCurrentArea(mapId, candidateCell) != bot.Player.CurrentArea ||
                IsUnsafeStep(runtime, bot, candidateCell, bot.Player.CurrentArea, now))
            {
                continue;
            }

            bot.Player.Position = candidate;
            bot.Player.Cell = candidateCell;
            var velocity = ScaledWalkVelocity(dirX, dirY, multiplier);
            bot.Player.Velocity = velocity;
            if (velocity.X > 0.1f)
            {
                bot.Player.Rotation = 180f;
            }
            else if (velocity.X < -0.1f)
            {
                bot.Player.Rotation = 0f;
            }
            movement = new BotMovementResult
            {
                BotPlayerId = bot.PlayerId,
                FromArea = bot.Player.CurrentArea,
                ToArea = bot.Player.CurrentArea,
                ToCell = candidateCell,
                Position = candidate,
                Velocity = velocity,
                Rotation = bot.Player.Rotation,
                IsAreaTransition = false
            };
            return true;
        }

        bot.SwarmDodgeHoldUntilUtc = DateTime.MinValue;
        return false;
    }

    internal static bool IsUnsafeStep(MatchRuntime runtime, Bot bot, Cell targetCell, AreaType targetArea, DateTime nowUtc)
    {
        if (targetArea != bot.Player.CurrentArea && runtime.Closures.IsAreaClosed(targetArea))
        {
            return true;
        }

        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        int targetDistance = SwarmPressureField.GetDistance(targetCell);
        if (targetDistance <= safeDistance)
        {
            return false;
        }

        var currentCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!);
        return targetDistance > SwarmPressureField.GetDistance(currentCell);
    }

    private void ChooseNewWanderTarget(MatchRuntime runtime, Bot bot, IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var mapId = Config.SWARM_MATCH_MAP;
        bot.ClearPath();

        bool needsGuardianOrb = !bot.Player.Orbs.GetOrderedOrbs().Any(item => OrbData.IsOrbItem(item.ItemId));

        if (bot.Player.CurrentArea.IsCorridor() && TryStartCorridorExitPath(runtime, bot))
        {
            return;
        }
        if (needsGuardianOrb)
        {
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.6);
            return;
        }

        var destination = ChooseWanderDestination(runtime, bot, playerAreas);
        if (destination == AreaType.None)
        {
            return;
        }
        if (destination == bot.Player.CurrentArea)
        {
            bot.LoopWaitUntil = GetRandomWaitDeadline(Config.SWARM_BOT_ROOM_DWELL_MIN_SECONDS, Config.SWARM_BOT_ROOM_DWELL_MAX_SECONDS);
            return;
        }

        var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.Player.CurrentArea, destination);
        var path = MapPathfinder.FindPath(mapId, bot.Player.CurrentArea, bot.Player.Cell!, destination, targetCell, a => IsClosedArea(runtime, a));
        if (path == null || path.Count == 0)
        {
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.6);
            logger.LogDebug("봇 배회 경로 실패: BotId={Bot}, {From} → {To}", bot.PlayerId, bot.Player.CurrentArea, destination);
            return;
        }

        bot.SetPath(path);
        bot.MovementDestination = destination;
        bot.LoopWaitUntil = GetRandomWaitDeadline(0.25, 0.6);
        logger.LogInformation("Bot wander move: BotId={Bot}, {From}->{To}, Steps={Steps}", bot.PlayerId, bot.Player.CurrentArea, destination, path.Count);
    }

    private bool TryStartCorridorExitPath(MatchRuntime runtime, Bot bot)
    {
        var mapId = Config.SWARM_MATCH_MAP;
        var exitAreas = GetOpenBotDestinationAreas(runtime).Where(area => area != bot.Player.CurrentArea).ToList();
        var adjacentExitAreas = exitAreas.Where(area => GameAreaConnectionData.IsAdjacent(mapId, bot.Player.CurrentArea, area)).ToList();
        if (adjacentExitAreas.Count > 0)
        {
            exitAreas = adjacentExitAreas;
        }

        var exit = exitAreas
            .Select(area => new
            {
                Area = area,
                Path = MapPathfinder.FindPath(
                    mapId,
                    bot.Player.CurrentArea,
                    bot.Player.Cell!,
                    area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.Player.CurrentArea, area)
                    ?? GameMapData.GetAreaSpawnCell(mapId, area),
                    candidate => IsClosedArea(runtime, candidate))
            })
            .Where(candidate => candidate.Path is { Count: > 0 })
            .OrderBy(candidate => candidate.Path!.Count)
            .ThenBy(candidate => CountAreaPressure(runtime, candidate.Area))
            .ThenBy(candidate => (int)candidate.Area)
            .FirstOrDefault();

        if (exit?.Path == null)
        {
            return false;
        }

        bot.SetPath(exit.Path);
        bot.MovementDestination = exit.Area;
        bot.LoopWaitUntil = DateTime.MinValue;
        logger.LogDebug("Bot corridor exit: BotId={Bot}, {From}->{To}, Steps={Steps}", bot.PlayerId, bot.Player.CurrentArea, exit.Area, exit.Path.Count);
        return true;
    }

    private List<AreaType> GetOpenBotDestinationAreas(MatchRuntime runtime)
    {
        return GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() && !IsClosedArea(runtime, area))
            .ToList();
    }

    private AreaType ChooseWanderDestination(MatchRuntime runtime, Bot bot, IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var rooms = GetOpenBotDestinationAreas(runtime);
        if (rooms.Count == 0)
        {
            return AreaType.None;
        }
        if (Random.Shared.NextDouble() < Config.SWARM_BOT_WANDER_SCATTER_PROBABILITY)
        {
            var pop = CountRoomPopulations(rooms, playerAreas);
            return rooms.OrderBy(a => pop[a]).ThenBy(_ => Random.Shared.Next()).First();
        }
        var others = rooms.Where(a => a != bot.Player.CurrentArea).ToList();
        return others.Count > 0 ? others[Random.Shared.Next(others.Count)] : AreaType.None;
    }

    private DateTime GetRandomWaitDeadline(double minSeconds, double maxSeconds)
    {
        double waitSeconds = minSeconds + Random.Shared.NextDouble() * (maxSeconds - minSeconds);
        return DateTime.UtcNow.AddSeconds(waitSeconds);
    }

    private static Dictionary<AreaType, int> CountRoomPopulations(List<AreaType> rooms, IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var pop = rooms.ToDictionary(a => a, _ => 0);
        foreach (var area in playerAreas.Values)
        {
            if (pop.ContainsKey(area))
            {
                pop[area]++;
            }
        }
        return pop;
    }
}
