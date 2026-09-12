using System.Diagnostics;
using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.players.bots;

/// <summary>
///     전달받은 매치의 봇 경로·걷기·회피를 처리하고, 구역별 수신자에게 이동 결과를 전송한다.
///     호출자는 매치 잠금을 보유한다. 다른 매치 조회나 타이머 관리는 하지 않는다.
///     봇의 전술 판단은 전달받은 함수에 위임한다.
/// </summary>
internal class BotMovementService(
    ILogger<BotMovementService> logger)
{
    private const double WanderScatterProbability = 0.3;
    private const double BotRoomDwellMinSeconds = 1.25;
    private const double BotRoomDwellMaxSeconds = 2.25;
    private const float BotWalkSpeed = 5f;
    private const float IsoVerticalSpeedScale = 1f;

    /// <summary>소환석·부츠에 대한 봇 반응 지연. 사람이 먼저 반응할 여유를 둔다.</summary>
    public static readonly TimeSpan SummonStoneBotReactionDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>매치 잠금 안에서 봇 걸음을 확정하고 같은 순서로 바로 송신한다.</summary>
    public virtual void ProcessTick(MatchRuntime runtime, Func<long, BotMovementDecision> decideMovement)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot movement requires the match lock.");
        }
        if (runtime.IsEnded)
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        ArgumentNullException.ThrowIfNull(decideMovement);
        long matchingId = runtime.MatchingId;
        long tickStartedAt = Stopwatch.GetTimestamp();
        var sessions = runtime.GetSessions();
        var playerAreas = new Dictionary<long, AreaType>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerAreas.Add(player.PlayerId, player.CurrentArea);
        }
        double snapshotElapsedMilliseconds = Stopwatch.GetElapsedTime(tickStartedAt).TotalMilliseconds;
        BotWalkingTickResult result = ProcessBotMovementTick(
            runtime, runtime.Closures, playerAreas, runtime.GroundItems, decideMovement);

        // 모든 봇의 걸음을 확정한 뒤 같은 매치 잠금 안에서 결과를 순서대로 보낸다.
        long broadcastStartedAt = Stopwatch.GetTimestamp();
        foreach (var movement in result.Movements)
        {
            runtime.Bots.GetBot(movement.BotPlayerId)?.Player.AdvanceOrbOrbit(movement.Position);
        }
        foreach (var movement in result.Movements)
        {
            SendMovement(runtime, movement, sessions);
        }
        double broadcastElapsedMilliseconds = Stopwatch.GetElapsedTime(broadcastStartedAt).TotalMilliseconds;

        BotMovementMetricsBatch? batch = runtime.BotMovementMetrics.Record(
            matchingId,
            new BotMovementSample(
                Stopwatch.GetElapsedTime(tickStartedAt).TotalMilliseconds,
                snapshotElapsedMilliseconds,
                result.PlanningElapsedMilliseconds,
                result.WalkingElapsedMilliseconds,
                broadcastElapsedMilliseconds));
        if (batch != null)
            PublishBotMovementMetrics(batch);
    }
    private void PublishBotMovementMetrics(BotMovementMetricsBatch batch)
    {
        double snapshotP95Milliseconds = CalculatePercentile(
            batch.SnapshotSamples.OrderBy(value => value).ToArray(),
            0.95);
        double planningP95Milliseconds = CalculatePercentile(
            batch.PlanningSamples.OrderBy(value => value).ToArray(),
            0.95);
        double walkingP95Milliseconds = CalculatePercentile(
            batch.WalkingSamples.OrderBy(value => value).ToArray(),
            0.95);
        double broadcastP95Milliseconds = CalculatePercentile(
            batch.BroadcastSamples.OrderBy(value => value).ToArray(),
            0.95);
        logger.LogInformation(
            "Bot movement tick: MatchingId={MatchingId} avg={Avg:F1}ms max={Max:F1}ms over {Count} ticks; " +
            "p95 snapshot={SnapshotP95:F1}ms planning={PlanningP95:F1}ms walking={WalkingP95:F1}ms " +
            "broadcast={BroadcastP95:F1}ms",
            batch.MatchingId,
            batch.TotalElapsedMilliseconds / batch.TickSamples.Length,
            batch.MaxElapsedMilliseconds,
            batch.TickSamples.Length,
            snapshotP95Milliseconds,
            planningP95Milliseconds,
            walkingP95Milliseconds,
            broadcastP95Milliseconds);
    }

    private void SendMovement(
        MatchRuntime runtime, BotMovementEvent movement, IReadOnlyList<GameClientSession> sessions)
    {
        if (movement.IsAreaTransition)
        {
            using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(movement.BotPlayerId);
            foreach (var session in sessions)
            {
                if (session.PlayerId is > 0 && session.MatchingId == runtime.MatchingId &&
                    session.Player.CurrentArea == movement.FromArea)
                {
                    session.TrySend(leavePacket);
                }
            }

            var enteringBot = runtime.Bots.SynthesizeGameObjectInfo(movement.BotPlayerId);
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

        float orbOrbitPhase = runtime.Bots.GetBot(movement.BotPlayerId)?.Player.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            movement.BotPlayerId,
            movement.Position,
            movement.Velocity,
            movement.Rotation,
            movement.ToCell,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            orbOrbitPhase);
        foreach (var session in sessions)
        {
            if (session.PlayerId is > 0 && session.MatchingId == runtime.MatchingId &&
                session.Player.CurrentArea == movement.ToArea)
            {
                session.TrySend(movePacket);
            }
        }
    }

    public void DispatchExternalMovement(MatchRuntime runtime, BotMovementEvent movement)
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

        // 외부에서 확정한 이동은 정기 틱과 달리 오브 공전 위상을 갱신하지 않는다.
        SendMovement(runtime, movement, runtime.GetSessions());
    }

    private static double CalculatePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0d;

        double position = (sortedValues.Count - 1) * Math.Clamp(percentile, 0d, 1d);
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sortedValues[lowerIndex];
        double fraction = position - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }


    private static float DistanceSquared(Vector3f position, float x, float y)
    {
        float dx = position.X - x;
        float dy = position.Y - y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    ///     화면 좌표 진행 방향(정규화)의 타일 기준 등속 속력.
    ///     세로가 압축된 아이소메트릭 화면에서 어느 방향이든 타일 통과 속도가 BotWalkSpeed로 일정해진다
    ///     (플레이어 로컬 이동의 세로 보정과 동일 규칙).
    /// </summary>
    private static float ScaledWalkSpeed(float dirX, float dirY, float movementMultiplier = 1f)
    {
        float tileY = dirY / IsoVerticalSpeedScale;
        float tileFactor = (float)Math.Sqrt(dirX * dirX + tileY * tileY);
        float baseSpeed = BotWalkSpeed * Math.Max(0f, movementMultiplier);
        return tileFactor > 0.0001f ? baseSpeed / tileFactor : baseSpeed;
    }

    internal static float GetBotMovementSpeedMultiplier(Bot bot)
    {
        float wind = OrbData.GetWindMoveSpeedMultiplier(bot.Player.Orbs.GetAllItems());
        // 부츠 (#222 M4): 사람과 같은 10초 이속 버프.
        float boots = DateTime.UtcNow < bot.BootsSpeedUntilUtc
            ? Config.BOOTS_MOVE_SPEED_MULTIPLIER
            : 1f;
        // 빈손 이속: 사람과 같은 규칙 — 마지막 오브를 잃은 직후
        // 2초만 빨라지고 원복한다. 유예가 끝난 빈손은 잔상의 우선 표적이 되어 재건에 쫓긴다.
        float bare = bot.IsSwarmBareHanded && DateTime.UtcNow < bot.SwarmBareSpeedUntilUtc
            ? Config.SWARM_BARE_MOVE_SPEED_MULTIPLIER
            : 1f;
        float waveSlow = DateTime.UtcNow < bot.Player.WaveSlowUntilUtc
            ? OrbData.WaveSlowMoveSpeedMultiplier
            : 1f;
        return wind * boots * bare * waveSlow;
    }
    private static Vector3f ScaledWalkVelocity(float dirX, float dirY, float movementMultiplier = 1f)
    {
        float speed = ScaledWalkSpeed(dirX, dirY, movementMultiplier);
        return new Vector3f(dirX * speed, dirY * speed, 0f);
    }

    /// <summary>한 틱의 봇 이동 결과와 단계별 처리 시간.</summary>
    public class BotWalkingTickResult
    {
        public List<BotMovementEvent> Movements { get; } = new();
        public long PlanningBotId { get; set; }
        public double PlanningElapsedMilliseconds { get; set; }
        public double WalkingElapsedMilliseconds { get; set; }
    }

    /// <summary>
    ///     매치별로 한 봇의 경로를 재계산하고, 살아 있는 봇들은 기존 경로를 따라 이동한다.
    /// </summary>
    public BotWalkingTickResult ProcessBotMovementTick(MatchRuntime runtime, MatchAreaClosureState closureManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        MatchGroundItemState groundItems,
        Func<long, BotMovementDecision> decideMovement)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Bot movement requires the match lock.");
        if (runtime.IsEnded)
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        long matchingId = runtime.MatchingId;
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

        DateTime nowUtc = DateTime.UtcNow;
        foreach (var bot in activeBots)
        {
            // 수면 중에는 경로 판단·걷기·아이템 획득을 하지 않는다. AI가 깨우면 기존 경로를 이어 간다.
            if (bot.Player.IsSleeping)
            {
                bot.LastWalkStepTime = nowUtc;
                if (bot.Player.Velocity.X != 0f || bot.Player.Velocity.Y != 0f)
                {
                    bot.Player.Velocity = new Vector3f();
                    result.Movements.Add(new BotMovementEvent
                    {
                        BotPlayerId = bot.PlayerId,
                        FromArea = bot.Player.CurrentArea,
                        ToArea = bot.Player.CurrentArea,
                        ToCell = bot.Player.Cell!,
                        Position = bot.Player.Position!,
                        Velocity = bot.Player.Velocity,
                        Rotation = bot.Player.Rotation,
                        IsAreaTransition = false
                    });
                }
                continue;
            }

            BotMovementDecision movementDecision = decideMovement(bot.PlayerId);
            if (movementDecision.Mode == BotMovementMode.None)
            {
                bot.MovementMode = BotMovementMode.None;
                bot.Path.Clear();
                bot.PathIndex = 0;
                bot.LastWalkStepTime = nowUtc;
                continue;
            }

            bool canPlanThisTick = bot.PlayerId == result.PlanningBotId;
            // 피격 중에는 1.5초 모드 홀드를 무시하고 매 계획 차례마다 재계획한다 (#222) —
            // 와리가리/도주 지시가 홀드에 씹혀 맞으면서 서 있던 현상(매치 2386 -182) 수리.
            // 창은 판단 레이어(SwarmBotDamagedFleeSeconds)와 같은 6초.
            bool underFire = (nowUtc - bot.LastDamagedAtUtc).TotalSeconds <= 6d;
            if (canPlanThisTick &&
                (bot.MovementMode == BotMovementMode.None ||
                 nowUtc >= bot.MovementModeUntilUtc ||
                 underFire))
            {
                bool changed = bot.MovementMode != movementDecision.Mode;
                bot.MovementMode = movementDecision.Mode;
                // 스웜 아레나 봇은 회피가 본체라 5초 홀드로는 서 있는 것처럼 보인다.
                const double holdSeconds = 1.5;
                if (changed || bot.MovementModeUntilUtc <= nowUtc)
                    bot.MovementModeUntilUtc = nowUtc.AddSeconds(holdSeconds);

                bot.MovementDestination = movementDecision.DestinationArea;
                bot.Path = MapPathfinder.FindPath(
                               runtime.Bots.MapId,
                               bot.Player.CurrentArea,
                               bot.Player.Cell!,
                               movementDecision.DestinationArea,
                               movementDecision.DestinationCell)
                           ?? [];
                bot.PathIndex = 0;
            }

            TrackIdleTime(bot, movementDecision, nowUtc);

            if (bot.PathIndex >= bot.Path.Count)
            {
                // 유휴 배회 (#222): 도착 대기(Return/Escort·경로 0) 상태로 수십 초 서 있던
                // 현상(유휴 감시 실측) — 4초 이상 제자리면 주변 셀로 서성인다.
                TryStartIdleWander(runtime, bot, matchingId, nowUtc);
                if (bot.PathIndex >= bot.Path.Count)
                {
                    bot.LastWalkStepTime = nowUtc;
                    continue;
                }
            }

            var movement = WalkStep(runtime,
                bot,
                matchingId,
                closureManager,
                playerAreas,
                canPlanThisTick);
            if (movement != null)
                result.Movements.Add(movement);
        }

        return result;
    }
    /// <summary>
    ///     유휴 감시 (#222): 6초 이상 제자리인 봇의 상태(모드·경로·홀드)를 10초에 한 번 남긴다.
    ///     "가만히 서 있는 봇" 신고가 반복되는데 이동은 로그에 안 남아 원인 특정이 안 됐다.
    /// </summary>
    private void TrackIdleTime(Bot bot, BotMovementDecision directive, DateTime nowUtc)
    {
        const float movedThresholdSquared = 0.01f;
        if (bot.IdleWatchLastPosition == null ||
            DistanceSquared(bot.IdleWatchLastPosition, bot.Player.Position!.X, bot.Player.Position!.Y) >
            movedThresholdSquared)
        {
            bot.IdleWatchLastPosition = new Vector3f(bot.Player.Position!.X, bot.Player.Position!.Y, 0f);
            bot.IdleWatchLastMovedAtUtc = nowUtc;
            return;
        }

        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 6d ||
            (nowUtc - bot.IdleWatchLastLoggedAtUtc).TotalSeconds < 10d)
            return;

        bot.IdleWatchLastLoggedAtUtc = nowUtc;
        logger.LogInformation(
            "Swarm bot idle: BotId={BotId}, Area={Area}, IdleSeconds={IdleSeconds:F0}, " +
            "Mode={Mode}, DirectiveArea={DirectiveArea}, PathRemaining={PathRemaining}",
            bot.PlayerId,
            bot.Player.CurrentArea,
            (nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds,
            directive.Mode,
            directive.DestinationArea,
            Math.Max(0, bot.Path.Count - bot.PathIndex));
    }

    /// <summary>유휴 배회 (#222): 제자리 4초 이상이면 같은 구역 인근 셀로 짧은 산책 경로를 만든다.</summary>
    private void TryStartIdleWander(MatchRuntime runtime, Bot bot, long matchingId, DateTime nowUtc)
    {
        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 4d || nowUtc < bot.NextIdleWanderAtUtc)
            return;

        bot.NextIdleWanderAtUtc = nowUtc.AddSeconds(3d);
        MapId mapId = runtime.Bots.MapId;
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

            bot.Path = path;
            bot.PathIndex = 0;
            return;
        }
    }

    private bool HasOpenNonCorridorRefuge(MatchRuntime runtime, long matchingId, MatchAreaClosureState closureManager)
    {
        return GameMapData.GetAreas(runtime.Bots.MapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Any(area => area != AreaType.None && !area.IsCorridor() && !closureManager.IsAreaClosed(area));
    }

    /// <summary>
    ///     지역 혼잡도. 목적지를 고르는 봇 자신은 제외해야 한다. 자기가 선 방의 점수를
    ///     스스로 깎으면 두 방을 1초 간격으로 왕복한다.
    /// </summary>
    private int CountAreaPressure(MatchRuntime runtime, long matchingId, AreaType area, long excludeBotPlayerId = 0)
    {
        return runtime.Bots.GetBots().Count(other =>
            {
                if (other.Player.IsEliminated || (excludeBotPlayerId != 0 && other.PlayerId == excludeBotPlayerId))
                    return false;

                // Travelling bots occupy their committed destination; idle bots occupy their current room.
                AreaType committedArea = other.EvacuationDestination != AreaType.None
                    ? other.EvacuationDestination
                    : other.MovementDestination != AreaType.None
                        ? other.MovementDestination
                        : other.Player.CurrentArea;
                return committedArea == area;
            });
    }

    private static bool IsClosedArea(MatchAreaClosureState closureManager, long matchingId, AreaType area)
    {
        if (area == AreaType.None)
            return false;

        return closureManager.IsAreaClosed(area);
    }
    /// <summary>
    ///     봇 한 명의 walking step 처리. 경로가 없으면 새 wander 타겟 선택.
    ///     영역 경계도 인접 셀까지 연속 보행하며, 도착 셀을 기준으로 영역 변경 이벤트를 반환한다.
    ///     일반 셀 walk는 진행 방향 + 속도 포함 MOVE 이벤트 반환.
    /// </summary>
    private BotMovementEvent? WalkStep(MatchRuntime runtime, Bot bot, long matchingId, MatchAreaClosureState closureManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        bool allowPathPlanning)
    {
        var now = DateTime.UtcNow;
        float deltaSec = (float)(now - bot.LastWalkStepTime).TotalSeconds;
        if (deltaSec <= 0) deltaSec = 0.25f;
        bot.LastWalkStepTime = now;

        // 투사체 회피 반사 (#232 §9): 경로·휴식·대기보다 먼저 — 이 자리를 지나갈 태양 투사체가
        // 있으면 그 직선의 수직으로 한 걸음 비켜선다. 경로는 버리지 않는다: 다음 틱에 비켜선 자리에서
        // 다음 웨이포인트로 이어 걷는다.
        if (TryDodgeStep(runtime, bot, matchingId, now, deltaSec, out var dodgeMovement))
            return dodgeMovement;

        // issue22 디버그: 도착 후 대기 중이면 walking 스킵
        if (now < bot.LoopWaitUntil) return null;

        // 경로 없거나 완료 → 새 타겟 결정
        if (bot.Path.Count == 0 || bot.PathIndex >= bot.Path.Count)
        {
            if (!allowPathPlanning) return null;
            ChooseNewWanderTarget(runtime, bot, matchingId, closureManager, playerAreas);
            if (bot.Path.Count == 0) return null;

            // 영역 도착 휴식처럼 대기를 설정한 결정은 다음 틱부터 걷는다. 같은 틱에 출발하면
            // 휴식이 무력화되어 발소리와 walk 애니가 끊긴다.
            // 반대로 대기가 없는 결정(방 안 잔상 추적)까지 한 틱을 쉬면, 1~2스텝짜리 짧은
            // 경로에서는 세 틱 중 한 틱을 멈춰 이동이 뚝뚝 끊겨 보인다.
            if (now < bot.LoopWaitUntil) return null;
        }

        var nextStep = bot.Path[bot.PathIndex];
        var mapId = runtime.Bots.MapId;
        var fromArea = bot.Player.CurrentArea;
        bool reachedStep = false;

        // 잠긴 문 통과 차단 (유저 제보: 봇이 문 열리기 전에 들어온다).
        // 사람과 같은 판정을 쓴다 — 이 전이를 관장하는 문 하나만 보고, 그 문이 닫혀 있으면 버린다.
        if (nextStep.Area != bot.Player.CurrentArea)
        {
            var transitionDoor = GameDoorData.GetDoorForTransition(
                bot.Player.CurrentArea, nextStep.Area, bot.Player.Cell!, nextStep.Cell);
            if (transitionDoor != null && !runtime.Doors.IsDoorOpen(transitionDoor.DoorId))
            {
                bot.Path.Clear();
                bot.PathIndex = 0;
                bot.MovementDestination = AreaType.None;
                bot.EvacuationDestination = AreaType.None;
                // 문 앞에서 기다린다 — 해제 채널링(ProcessDoorInteractions)이 돌 시간을 준다.
                bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.4);
                if (bot.LastLockedDoorBlockArea != nextStep.Area)
                {
                    bot.LastLockedDoorBlockArea = nextStep.Area;
                    logger.LogInformation(
                        "Bot blocked at closed door: MatchingId={MatchingId}, BotId={BotId}, " +
                        "From={From}, To={To}, DoorId={DoorId}",
                        matchingId, bot.PlayerId, bot.Player.CurrentArea, nextStep.Area, transitionDoor.DoorId);
                }

                return null;
            }
        }

        // Walk every waypoint at the same speed. An area transition is just the adjacent cell across a door.
        var targetPos = MapCoordinateConverter.CellToWorld(mapId, nextStep.Cell);
        float dx = targetPos.X - bot.Player.Position!.X;
        float dy = targetPos.Y - bot.Player.Position!.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float maxDist = BotWalkSpeed * GetBotMovementSpeedMultiplier(bot) * deltaSec;
        if (dist >= 0.01f)
            maxDist = ScaledWalkSpeed(dx / dist, dy / dist, GetBotMovementSpeedMultiplier(bot)) * deltaSec;

        Vector3f newPosition;
        Vector3f velocity;

        // 벽 판정 (유저 제보: 봇이 문이 아니라 벽으로 넘어다닌다).
        // WalkStep은 웨이포인트로 직선 이동만 했다 — 경로가 한 칸이라도 어긋나면 그대로 통과한다.
        // 다음 웨이포인트가 비보행이면 그 경로는 이미 틀린 것이므로 버리고 다시 짠다.
        // 이미 벽 안에 서 있는 개체는 막지 않는다 — 막으면 영영 못 빠져나온다.
        if (!GameMapData.IsMoveablePosition(mapId, nextStep.Cell) &&
            GameMapData.IsMoveablePosition(mapId, bot.Player.Cell!))
        {
            bot.Path.Clear();
            bot.PathIndex = 0;
            bot.MovementDestination = AreaType.None;
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.4, 0.9);
            return null;
        }

        if (dist <= maxDist || dist < 0.01f)
        {
            // 도달 → 다음 인덱스
            newPosition = targetPos;
            bot.Player.Cell = nextStep.Cell;
            bot.Player.Position = newPosition;
            bot.PathIndex++;
            reachedStep = true;
            velocity = new Vector3f(0f, 0f, 0f);
            if (bot.PathIndex < bot.Path.Count)
            {
                var followingPos = MapCoordinateConverter.CellToWorld(mapId, bot.Path[bot.PathIndex].Cell);
                float nextDx = followingPos.X - newPosition.X;
                float nextDy = followingPos.Y - newPosition.Y;
                float nextDist = (float)Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextDist > 0.01f)
                {
                    velocity = ScaledWalkVelocity(
                        nextDx / nextDist,
                        nextDy / nextDist,
                        GetBotMovementSpeedMultiplier(bot));

                    // 웨이포인트에 스냅하면 이번 틱에 갈 수 있었던 거리가 버려져 그 틱만 느려진다.
                    // 셀을 지날 때마다 반복되므로 이동이 움찔거려 보인다. 남은 몫을 다음
                    // 웨이포인트 방향으로 이어서 소비한다.
                    float leftover = maxDist - dist;
                    if (leftover > 0f)
                    {
                        float carry = Math.Min(leftover, nextDist);
                        newPosition = new Vector3f(
                            newPosition.X + nextDx / nextDist * carry,
                            newPosition.Y + nextDy / nextDist * carry,
                            0f);
                        bot.Player.Position = newPosition;
                    }
                }
            }
        }
        else
        {
            float dirX = dx / dist;
            float dirY = dy / dist;
            newPosition = new Vector3f(
                bot.Player.Position!.X + dirX * maxDist,
                bot.Player.Position!.Y + dirY * maxDist,
                0f);
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

        // velocity.X 부호에 따라 Rotation 갱신 (실제 플레이어 PlayerMovement.cs와 동일 규칙).
        // shouldFlip = velocity.X > 0 → rotation = 180 (오른쪽 보기), 아니면 0 (왼쪽 보기).
        // |velocity.X| < 0.1 시에는 직전 Rotation 유지(떨림 방지 — 클라 IsFlip 갱신 가드와 일치).
        if (velocity.X > 0.1f) bot.Player.Rotation = 180f;
        else if (velocity.X < -0.1f) bot.Player.Rotation = 0f;

        return new BotMovementEvent
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

    /// <summary>
    ///     투사체 회피 (#232 §9, "제자리 좌우 와리가리 금지"): 위협이 있으면 리졸버가 준 방향으로
    ///     한 걸음 비켜서고 그 방향을 위협이 지나갈 때까지 커밋한다. 커밋 중에 띠 밖으로 나가 위협이 사라지면
    ///     원래 경로로 되돌아가지 않고 제자리에 선다 — 되돌아가면 다시 띠에 들어가 다시 비켜서는 떨림이 된다.
    ///     비켜선 칸이 벽이거나 다른 구역이면 반대쪽을 시도하고, 둘 다 막히면 회피 없이 원래 걸음.
    ///     경로·경로 인덱스는 손대지 않는다.
    /// </summary>
    /// <returns>true면 이번 틱은 회피 층이 처리했다(경로 걸음 없음). movement는 보낼 이벤트, 없으면 null.</returns>
    private bool TryDodgeStep(MatchRuntime runtime,
        Bot bot, long matchingId, DateTime now, float deltaSec, out BotMovementEvent? movement)
    {
        movement = null;
        if (bot.Player.CurrentArea == AreaType.None)
            return false;

        bool committed = now < bot.SwarmDodgeHoldUntilUtc;
        var advice = BotDodgePolicy.GetDodgeDirection(
            runtime.SunCrossfireShapes, bot.PlayerId, bot.Player.Position!, bot.Player.CurrentArea, now);
        if (advice == null)
        {
            if (!committed)
                return false;
            // 띠 밖, 위협은 아직 안 지남 — 서서 기다린다. 걷기 패킷을 한 번 0으로 끊어 발소리·걷기 애니를 멈춘다.
            if (bot.Player.Velocity.X != 0f || bot.Player.Velocity.Y != 0f)
            {
                bot.Player.Velocity = new Vector3f(0f, 0f, 0f);
                movement = new BotMovementEvent
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

        // 커밋 방향 우선 — 새 조언이 반대쪽이 아니면 원래 방향을 유지한다(중간에 방향을 뒤집으면 그것도 떨림).
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
            bot.SwarmDodgeHoldUntilUtc = holdUntil;

        var mapId = runtime.Bots.MapId;
        float multiplier = GetBotMovementSpeedMultiplier(bot);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            float sign = attempt == 0 ? 1f : -1f;
            float dirX = dirX0 * sign;
            float dirY = dirY0 * sign;
            if (attempt == 1)
            {
                // 반대쪽으로 갈 수밖에 없으면 커밋도 그쪽으로 바꾼다.
                bot.SwarmDodgeDirectionX = dirX;
                bot.SwarmDodgeDirectionY = dirY;
            }
            float step = ScaledWalkSpeed(dirX, dirY, multiplier) * deltaSec;
            var candidate = new Vector3f(bot.Player.Position!.X + dirX * step, bot.Player.Position!.Y + dirY * step, 0f);
            var candidateCell = MapCoordinateConverter.WorldToCell(mapId, candidate);
            if (!GameMapData.IsMoveablePosition(mapId, candidateCell) ||
                GameMapData.GetCurrentArea(mapId, candidateCell) != bot.Player.CurrentArea)
                continue;


            bot.Player.Position = candidate;
            bot.Player.Cell = candidateCell;
            var velocity = ScaledWalkVelocity(dirX, dirY, multiplier);
            bot.Player.Velocity = velocity;
            if (velocity.X > 0.1f) bot.Player.Rotation = 180f;
            else if (velocity.X < -0.1f) bot.Player.Rotation = 0f;
            movement = new BotMovementEvent
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

        // 양쪽 다 막혔다 — 커밋을 풀고 원래 걸음으로 돌아간다.
        bot.SwarmDodgeHoldUntilUtc = DateTime.MinValue;
        return false;
    }

    /// <summary>
    ///     봇이 도착했거나 경로가 비었을 때 다음 목적지 선택 + 경로 계산.
    ///     배회(ChooseWanderDestination): 따라가기(타겟 방)·흩어지기(최저 인원 방)·임의 방. 복도는 목적지가 아니라 통과만(transit).
    /// </summary>
    private void ChooseNewWanderTarget(MatchRuntime runtime, Bot bot, long matchingId, MatchAreaClosureState closureManager,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var mapId = runtime.Bots.MapId;
        bot.Path.Clear();
        bot.PathIndex = 0;

        bool needsGuardianOrb = !bot.Player.Orbs.GetOrderedOrbs().Any(item => OrbData.IsOrbItem(item.ItemId));

        // 복도는 통로다. 가장 가까운 방으로 나간다.
        if (bot.Player.CurrentArea.IsCorridor() &&
            TryStartCorridorExitPath(runtime, bot, matchingId, mapId, closureManager))
        {
            return;
        }

        // Starting orbs are granted before the first movement tick. Keep the guard for an
        // unexpected initialization failure, but never route to RNG pickup locations.
        if (needsGuardianOrb)
        {
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.6);
            return;
        }

        var destination = ChooseWanderDestination(runtime, bot, matchingId, mapId, playerAreas, closureManager);
        if (destination == AreaType.None) return;
        if (destination == bot.Player.CurrentArea)
        {
            // 이미 원하는 방에 있음 → 잠시 머물며 회복/기척.
            // (즉시 재결정 시 흩어지기 확률이 매 틱 굴러 곧바로 나가버리는 문제 방지)
            bot.LoopWaitUntil = GetRandomWaitDeadline(BotRoomDwellMinSeconds, BotRoomDwellMaxSeconds);
            return;
        }

        var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.Player.CurrentArea, destination)
            ?? GameMapData.GetAreaSpawnCell(mapId, destination);

        var path = MapPathfinder.FindPath(mapId, bot.Player.CurrentArea, bot.Player.Cell!,
            destination, targetCell,
            a => IsClosedArea(closureManager, matchingId, a));
        if (path == null || path.Count == 0)
        {
            bot.LoopWaitUntil = GetRandomWaitDeadline(0.8, 1.6);
            logger.LogDebug("봇 배회 경로 실패: BotId={Bot}, {From} → {To}",
                bot.PlayerId, bot.Player.CurrentArea, destination);
            return;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.MovementDestination = destination;
        bot.LoopWaitUntil = GetRandomWaitDeadline(0.25, 0.6);
        logger.LogInformation(
            "Bot wander move: BotId={Bot}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.Player.CurrentArea, destination, path.Count);
    }

    private bool TryStartCorridorExitPath(MatchRuntime runtime, Bot bot, long matchingId, MapId mapId,
        MatchAreaClosureState closureManager)
    {
        var exitAreas = GetOpenBotDestinationAreas(matchingId, mapId, closureManager)
            .Where(area => area != bot.Player.CurrentArea)
            .ToList();

        // 복도에서는 인접한 방으로 나가면 충분하다. 열린 지역 전체에 길을 찾으면 봇마다
        // A*가 지역 수만큼 돌아 이동 틱이 200ms 넘게 튀고, 그 사이 틱이 스킵되어 봇 위치
        // 브로드캐스트가 끊긴다. 인접한 곳이 없을 때만 전체로 넓힌다.
        var adjacentExitAreas = exitAreas
            .Where(area => GameAreaConnectionData.IsAdjacent(mapId, bot.Player.CurrentArea, area))
            .ToList();
        if (adjacentExitAreas.Count > 0)
            exitAreas = adjacentExitAreas;

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
                    candidate => IsClosedArea(closureManager, matchingId, candidate))
            })
            .Where(candidate => candidate.Path is { Count: > 0 })
            .OrderBy(candidate => candidate.Path!.Count)
            .ThenBy(candidate => CountAreaPressure(runtime, matchingId, candidate.Area))
            .ThenBy(candidate => (int)candidate.Area)
            .FirstOrDefault();

        if (exit?.Path == null)
            return false;

        bot.Path = exit.Path;
        bot.PathIndex = 0;
        bot.MovementDestination = exit.Area;
        bot.LoopWaitUntil = DateTime.MinValue;
        logger.LogDebug(
            "Bot corridor exit: BotId={Bot}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.Player.CurrentArea, exit.Area, exit.Path.Count);
        return true;
    }

    private List<AreaType> GetOpenBotDestinationAreas(long matchingId, MapId mapId,
        MatchAreaClosureState closureManager)
    {
        return GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() &&
                           !IsClosedArea(closureManager, matchingId, area))
            .ToList();
    }

    /// <summary>
    ///     배회 폴백 목적지: 잔상 사냥·캠프 순례가 목적지를 못 정할 때만 온다.
    ///     확률적으로 최저 인원 방으로 흩어지고(뭉침 방지), 아니면 현재와 다른 임의 방.
    /// </summary>
    private AreaType ChooseWanderDestination(MatchRuntime runtime, Bot bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas, MatchAreaClosureState closureManager)
    {
        var rooms = GetOpenBotDestinationAreas(matchingId, mapId, closureManager);
        if (rooms.Count == 0) return AreaType.None;

        // 흩어지기: 최저 인원 방으로 — 봇이 한 방에 뭉쳐 잔상을 경합하지 않게.
        if (Random.Shared.NextDouble() < WanderScatterProbability)
        {
            var pop = CountRoomPopulations(rooms, playerAreas);
            return rooms.OrderBy(a => pop[a]).ThenBy(_ => Random.Shared.Next()).First();
        }

        // 현재와 다른 임의 방
        var others = rooms.Where(a => a != bot.Player.CurrentArea).ToList();
        return others.Count > 0 ? others[Random.Shared.Next(others.Count)] : AreaType.None;
    }

    private double RandomRange(double min, double max)
    {
        return min + Random.Shared.NextDouble() * (max - min);
    }

    private DateTime GetRandomWaitDeadline(double minSeconds, double maxSeconds)
    {
        return DateTime.UtcNow.AddSeconds(RandomRange(minSeconds, maxSeconds));
    }

    /// <summary>각 방의 현재 인원수(봇+인간) 집계 — 흩어지기 목적지 선택용.</summary>
    private static Dictionary<AreaType, int> CountRoomPopulations(List<AreaType> rooms,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var pop = rooms.ToDictionary(a => a, _ => 0);
        foreach (var area in playerAreas.Values)
            if (pop.ContainsKey(area)) pop[area]++;
        return pop;
    }
}
