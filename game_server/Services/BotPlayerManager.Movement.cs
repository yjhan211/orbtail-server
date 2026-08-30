using System.Diagnostics;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     봇 이동 AI. 자기 직책 발견 구역 우선 순회 + 폐쇄 회피.
///     #26: "허수아비" 무작위 이동 → 직책별 목적성 동선으로 폴리싱.
///     #125: 영역 전환/영역 내 셀 wander 시 BotMovementEvent 반환 → GameServer가 패킷 브로드캐스트.
///     #127: walking pathfinding — 셀 단위 walk + 영역 경계 통과 BFS. WanderInArea 제거.
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>Bot movement speed matches the player fixed movement speed.</summary>
    private const float BotWalkSpeed = 5f;

    /// <summary>한 번의 사냥 판단에서 실제로 길을 찾아볼 지역 수. 나머지는 사전 점수로 걸러낸다.</summary>
    private const int MaxHuntPathCandidates = 4;

    /// <summary>아이소메트릭 세로 속도 보정 — 클라 PlayerMovement.isoVerticalSpeedScale과 같은 값을 유지해야 한다.</summary>
    private const float IsoVerticalSpeedScale = 1f;

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

    private static float GetBotMovementSpeedMultiplier(BotPlayerState bot)
    {
        float wind = Math.Max(1f, bot.WindMoveSpeedMultiplier);
        // 부츠 (#222 M4): 사람과 같은 10초 이속 버프.
        float boots = DateTime.UtcNow < bot.BootsSpeedUntilUtc
            ? Config.BOOTS_MOVE_SPEED_MULTIPLIER
            : 1f;
        // 빈손 이속 (#223 → #229 12단계): 사람과 같은 규칙 — 마지막 오브를 잃은 직후
        // 2초만 빨라지고 원복한다. 유예가 끝난 빈손은 잔상의 우선 표적이 되어 재건에 쫓긴다.
        float bare = bot.IsSwarmBareHanded && DateTime.UtcNow < bot.SwarmBareSpeedUntilUtc
            ? Config.SWARM_BARE_MOVE_SPEED_MULTIPLIER
            : 1f;
        return wind * boots * bare * GetBotWaveSlowMultiplier(bot);
    }

    private static float GetBotWaveSlowMultiplier(BotPlayerState bot)
    {
        return DateTime.UtcNow < bot.WaveSlowUntilUtc
            ? OrbData.WaveSlowMoveSpeedMultiplier
            : 1f;
    }
    private static Vector3f ScaledWalkVelocity(float dirX, float dirY, float movementMultiplier = 1f)
    {
        float speed = ScaledWalkSpeed(dirX, dirY, movementMultiplier);
        return new Vector3f(dirX * speed, dirY * speed, 0f);
    }


    /// <summary>#134 봇 walking 틱 결과.</summary>
    public class BotWalkingTickResult
    {
        public List<BotMovementEvent> Movements { get; } = new();
        public List<BotGroundItemPickup> GroundItemPickups { get; } = new();
        public long PlanningBotId { get; set; }
        public double PlanningElapsedMilliseconds { get; set; }
        public double WalkingElapsedMilliseconds { get; set; }
    }

    public void ApplyEnvironmentalCorruption(BotPlayerState bot, int corruptionDelta)
    {
        if (bot.IsEliminated || corruptionDelta == 0)
            return;

        bot.Corruption = Math.Clamp(bot.Corruption + corruptionDelta, 0, Config.MAX_CORRUPTION);
    }

    /// <summary>
    ///     #127: 봇 walking 틱(50ms). legacy mode 비활성 시 BotPathfinder 경로를 따라 셀 단위 이동.
    ///     Uses the same fixed movement speed 6.0 as the player and emits an equivalent G_TO_C_MOVE event each tick.
    /// </summary>
    public BotWalkingTickResult ProcessBotMovementTick(long matchingId, AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager,
        IReadOnlyDictionary<long, AreaType> humanAreas,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        IReadOnlyCollection<MonsterCombatTarget>? pveTargets,
        Func<long, long, SpotArenaBotDirective> spotArenaDirectiveProvider,
        SummonStoneManager? summonStoneManager = null)
    {
        var result = new BotWalkingTickResult();
        summonStoneManager ??= new SummonStoneManager();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        var activeBots = bots.Where(bot => !bot.IsEliminated).ToList();
        if (activeBots.Count == 0) return result;

        result.PlanningBotId = SelectMovementPlanningBot(matchingId, activeBots);

        // 전체 플레이어(인간 + 봇) 현재 영역 맵 — 봇 타겟 추적/떠보기 인원수 계산용.
        var playerAreas = new Dictionary<long, AreaType>(humanAreas);
        foreach (var b in activeBots)
            playerAreas[b.PlayerId] = b.CurrentArea;

        return ProcessSpotArenaBotMovement(
            matchingId,
            activeBots,
            result,
            closureManager,
            areaItemStockManager,
            inventoryManager,
            groundItemManager,
            summonStoneManager,
            playerAreas,
            pveTargets ?? [],
            spotArenaDirectiveProvider);
    }

    private BotWalkingTickResult ProcessSpotArenaBotMovement(
        long matchingId,
        IReadOnlyList<BotPlayerState> activeBots,
        BotWalkingTickResult result,
        AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        SummonStoneManager summonStoneManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        IReadOnlyCollection<MonsterCombatTarget> pveTargets,
        Func<long, long, SpotArenaBotDirective> directiveProvider)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (var bot in activeBots)
        {
            // 절단 실험 더미 (#226): AI 전체 정지 — 이동·픽업·탐색 없이 과녁으로만 서 있는다.
            if (bot.IsSwarmCutDummy)
            {
                bot.Path.Clear();
                bot.PathIndex = 0;
                continue;
            }

            if (TryAutoPickupGroundItem(
                    bot, matchingId, inventoryManager, groundItemManager, summonStoneManager, out var pickup) &&
                pickup.HasValue)
            {
                result.GroundItemPickups.Add(pickup.Value);
            }

            SpotArenaBotDirective currentDirective = directiveProvider(matchingId, bot.PlayerId);
            if (currentDirective.Mode == SpotArenaBotMode.None)
            {
                bot.SpotArenaMode = SpotArenaBotMode.None;
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
                (bot.SpotArenaMode == SpotArenaBotMode.None ||
                 nowUtc >= bot.SpotArenaModeUntilUtc ||
                 underFire))
            {
                bool changed = bot.SpotArenaMode != currentDirective.Mode;
                bot.SpotArenaMode = currentDirective.Mode;
                // 스웜 아레나 봇은 회피가 본체라 5초 홀드로는 서 있는 것처럼 보인다.
                const double holdSeconds = 1.5;
                if (changed || bot.SpotArenaModeUntilUtc <= nowUtc)
                    bot.SpotArenaModeUntilUtc = nowUtc.AddSeconds(holdSeconds);

                bot.MovementDestination = currentDirective.DestinationArea;
                bot.Path = BotPathfinder.FindPath(
                               GetMatchingMapId(matchingId),
                               bot.CurrentArea,
                               bot.Cell,
                               currentDirective.DestinationArea,
                               currentDirective.DestinationCell)
                           ?? [];
                bot.PathIndex = 0;
            }

            TrackSwarmBotIdle(bot, currentDirective, nowUtc);

            if (bot.PathIndex >= bot.Path.Count)
            {
                // 유휴 배회 (#222): 도착 대기(Return/Escort·경로 0) 상태로 수십 초 서 있던
                // 현상(유휴 감시 실측) — 4초 이상 제자리면 주변 셀로 서성인다.
                TryStartSwarmIdleWander(bot, matchingId, nowUtc);
                if (bot.PathIndex >= bot.Path.Count)
                {
                    bot.LastWalkStepTime = nowUtc;
                    continue;
                }
            }

            var movement = WalkStep(
                bot,
                matchingId,
                closureManager,
                areaItemStockManager,
                inventoryManager,
                playerAreas,
                pveTargets,
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
    private void TrackSwarmBotIdle(BotPlayerState bot, SpotArenaBotDirective directive, DateTime nowUtc)
    {
        const float movedThresholdSquared = 0.01f;
        if (bot.IdleWatchLastPosition == null ||
            DistanceSquared(bot.IdleWatchLastPosition, bot.Position.X, bot.Position.Y) >
            movedThresholdSquared)
        {
            bot.IdleWatchLastPosition = new Vector3f(bot.Position.X, bot.Position.Y, 0f);
            bot.IdleWatchLastMovedAtUtc = nowUtc;
            return;
        }

        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 6d ||
            (nowUtc - bot.IdleWatchLastLoggedAtUtc).TotalSeconds < 10d)
            return;

        bot.IdleWatchLastLoggedAtUtc = nowUtc;
        _logger.LogInformation(
            "Swarm bot idle: BotId={BotId}, Area={Area}, IdleSeconds={IdleSeconds:F0}, " +
            "Mode={Mode}, DirectiveArea={DirectiveArea}, PathRemaining={PathRemaining}, " +
            "InInteraction={InInteraction}, ExploreSpot={ExploreSpot}",
            bot.PlayerId,
            bot.CurrentArea,
            (nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds,
            directive.Mode,
            directive.DestinationArea,
            Math.Max(0, bot.Path.Count - bot.PathIndex),
            bot.IsChannelHeld,
            bot.SwarmExploreSpotId);
    }

    /// <summary>유휴 배회 (#222): 제자리 4초 이상이면 같은 구역 인근 셀로 짧은 산책 경로를 만든다.</summary>
    private void TryStartSwarmIdleWander(BotPlayerState bot, long matchingId, DateTime nowUtc)
    {
        if (bot.IsChannelHeld || bot.SwarmExploreSpotId != 0)
            return;
        if ((nowUtc - bot.IdleWatchLastMovedAtUtc).TotalSeconds < 4d || nowUtc < bot.NextIdleWanderAtUtc)
            return;

        bot.NextIdleWanderAtUtc = nowUtc.AddSeconds(3d);
        MapId mapId = GetMatchingMapId(matchingId);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var candidate = new Cell(
                bot.Cell.X + Random.Shared.Next(-3, 4),
                bot.Cell.Y + Random.Shared.Next(-3, 4));
            if (candidate.X == bot.Cell.X && candidate.Y == bot.Cell.Y)
                continue;
            if (!GameMapData.IsMoveablePosition(mapId, candidate) ||
                GameMapData.GetCurrentArea(mapId, candidate) != bot.CurrentArea)
                continue;

            var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell, bot.CurrentArea, candidate);
            if (path == null || path.Count == 0)
                continue;

            bot.Path = path;
            bot.PathIndex = 0;
            return;
        }
    }

    private long SelectMovementPlanningBot(long matchingId, IReadOnlyList<BotPlayerState> activeBots)
    {
        int cursor = _botMovementPlanningCursors.AddOrUpdate(
            matchingId,
            0,
            (_, current) => (current + 1) % activeBots.Count);
        return activeBots[cursor % activeBots.Count].PlayerId;
    }

    private bool HasOpenNonCorridorRefuge(long matchingId, AreaClosureManager closureManager)
    {
        var closure = closureManager.GetClientStateSnapshot(matchingId);
        var unavailableAreas = closure.ClosedAreas.Concat(closure.WarningAreas).ToHashSet();
        return GameMapData.GetAreas(GetMatchingMapId(matchingId))
            .Select(region => region.AreaType)
            .Distinct()
            .Any(area => area != AreaType.None && !area.IsCorridor() && !unavailableAreas.Contains(area));
    }

    /// <summary>
    ///     지역 혼잡도. 목적지를 고르는 봇 자신은 제외해야 한다. 자기가 선 방의 점수를
    ///     스스로 깎으면 두 방을 1초 간격으로 왕복한다 (2026-07-30 matching 1983에서
    ///     Storage↔Library 8회 진동 관측).
    /// </summary>
    private int CountAreaPressure(long matchingId, AreaType area, long excludeBotPlayerId = 0)
    {
        return _botStates.TryGetValue(matchingId, out var bots)
            ? bots.Count(other =>
            {
                if (other.IsEliminated || (excludeBotPlayerId != 0 && other.PlayerId == excludeBotPlayerId))
                    return false;

                // Travelling bots occupy their committed destination; idle bots occupy their current room.
                AreaType committedArea = other.EvacuationDestination != AreaType.None
                    ? other.EvacuationDestination
                    : other.MovementDestination != AreaType.None
                        ? other.MovementDestination
                        : other.CurrentArea;
                return committedArea == area;
            })
            : 0;
    }


    private static bool IsAreaClosingOrClosed(AreaClosureManager closureManager, long matchingId, AreaType area)
    {
        if (area == AreaType.None)
            return false;

        var closure = closureManager.GetClientStateSnapshot(matchingId);
        return closure.ClosedAreas.Contains(area) || closure.WarningAreas.Contains(area);
    }
    /// <summary>
    ///     봇 한 명의 walking step 처리. 경로가 없으면 새 wander 타겟 선택.
    ///     영역 경계도 인접 셀까지 연속 보행하며, 도착 셀을 기준으로 영역 변경 이벤트를 반환한다.
    ///     일반 셀 walk는 진행 방향 + 속도 포함 MOVE 이벤트 반환.
    /// </summary>
    private BotMovementEvent? WalkStep(BotPlayerState bot, long matchingId, AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager, InGameInventoryManager inventoryManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        IReadOnlyCollection<MonsterCombatTarget> pveTargets,
        bool allowPathPlanning)
    {
        var now = DateTime.UtcNow;
        float deltaSec = (float)(now - bot.LastWalkStepTime).TotalSeconds;
        if (deltaSec <= 0) deltaSec = 0.25f;
        bot.LastWalkStepTime = now;

        // 상호작용 중에는 walking 정지 (실제 플레이어 정지 동작과 동등).
        // ChannelHoldUntil 시각이 지나면 자동 해제.
        if (bot.IsChannelHeld)
        {
            if (now >= bot.ChannelHoldUntil) bot.IsChannelHeld = false;
            else
            {
                // 첫 진입 시 velocity 0 패킷 1회 발행 (이전 walking 패킷의 velocity가 그대로면 클라 발소리 잔존)
                if (bot.WalkVelocity.X != 0f || bot.WalkVelocity.Y != 0f)
                {
                    bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
                    return new BotMovementEvent
                    {
                        BotPlayerId = bot.PlayerId,
                        FromArea = bot.CurrentArea,
                        ToArea = bot.CurrentArea,
                        FromCell = bot.Cell,
                        ToCell = bot.Cell,
                        Position = bot.Position,
                        Velocity = new Vector3f(0f, 0f, 0f),
                        Rotation = bot.Rotation,
                        IsAreaTransition = false
                    };
                }
                return null;
            }
        }

        // 투사체 회피 반사 (#232 §9, 2026-08-18): 경로·휴식·대기보다 먼저 — 이 자리를 지나갈 태양 투사체가
        // 있으면 그 직선의 수직으로 한 걸음 비켜선다. 경로는 버리지 않는다: 다음 틱에 비켜선 자리에서
        // 다음 웨이포인트로 이어 걷는다. 상호작용(채널링) 중만 예외 — 사람도 채널링 중엔 못 움직인다.
        if (TryDodgeStep(bot, matchingId, now, deltaSec, out var dodgeMovement))
            return dodgeMovement;

        if (now < bot.RestUntil)
        {
            if (bot.WalkVelocity.X != 0f || bot.WalkVelocity.Y != 0f)
            {
                bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
                return new BotMovementEvent
                {
                    BotPlayerId = bot.PlayerId,
                    FromArea = bot.CurrentArea,
                    ToArea = bot.CurrentArea,
                    FromCell = bot.Cell,
                    ToCell = bot.Cell,
                    Position = bot.Position,
                    Velocity = new Vector3f(0f, 0f, 0f),
                    Rotation = bot.Rotation,
                    IsAreaTransition = false
                };
            }

            return null;
        }

        if (bot.PendingForcedInteractId > 0 && bot.PendingForcedInteractArea != AreaType.None)
        {
            if (!allowPathPlanning) return null;

            if (TryStartBotInteractPath(bot, matchingId, bot.PendingForcedInteractArea,
                    bot.PendingForcedInteractId, closureManager))
                return null;

            bot.PendingForcedInteractArea = AreaType.None;
            bot.PendingForcedInteractId = 0;
        }

        // issue22 디버그: 도착 후 대기 중이면 walking 스킵
        if (now < bot.LoopWaitUntil) return null;

        // 경로 없거나 완료 → 새 타겟 결정
        if (bot.Path.Count == 0 || bot.PathIndex >= bot.Path.Count)
        {
            // #134 — 도착 후 RNG 채집이 아직 안 됐으면 walking 보류 (ProcessBotMissionTick이 PendingRngInteractId 처리 후 0으로 클리어할 때까지 대기).
            if (bot.PendingRngInteractId != 0) return null;

            if (!allowPathPlanning) return null;
            ChooseNewWanderTarget(bot, matchingId, closureManager, areaItemStockManager, inventoryManager, playerAreas,
                pveTargets);
            if (bot.Path.Count == 0) return null;

            // 영역 도착 휴식처럼 대기를 설정한 결정은 다음 틱부터 걷는다. 같은 틱에 출발하면
            // 휴식이 무력화되어 발소리와 walk 애니가 끊긴다.
            // 반대로 대기가 없는 결정(방 안 잔상 추적)까지 한 틱을 쉬면, 1~2스텝짜리 짧은
            // 경로에서는 세 틱 중 한 틱을 멈춰 이동이 뚝뚝 끊겨 보인다.
            if (now < bot.LoopWaitUntil) return null;
        }

        var nextStep = bot.Path[bot.PathIndex];
        var mapId = GetMatchingMapId(matchingId);
        var fromArea = bot.CurrentArea;
        var fromCell = bot.Cell;
        bool reachedStep = false;


        // 잠긴 문 통과 차단 (2026-08-16 유저 제보: 봇이 문 열리기 전에 들어온다).
        // 사람과 같은 판정을 쓴다 — 이 전이를 관장하는 문 하나만 보고, 그 문이 닫혀 있으면 버린다.
        if (nextStep.Area != bot.CurrentArea)
        {
            var transitionDoor = GameDoorData.GetDoorForTransition(
                bot.CurrentArea, nextStep.Area, bot.Cell, nextStep.Cell);
            if (transitionDoor != null && !IsDoorOpenForBot(matchingId, transitionDoor.DoorId))
            {
                bot.Path.Clear();
                bot.PathIndex = 0;
                bot.MovementDestination = AreaType.None;
                bot.EvacuationDestination = AreaType.None;
                // 문 앞에서 기다린다 — 해제 채널링(ProcessSwarmBotDoorUnlocks)이 돌 시간을 준다.
                bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.4);
                if (bot.LastLockedDoorBlockArea != nextStep.Area)
                {
                    bot.LastLockedDoorBlockArea = nextStep.Area;
                    _logger.LogInformation(
                        "Bot blocked at closed door: MatchingId={MatchingId}, BotId={BotId}, " +
                        "From={From}, To={To}, DoorId={DoorId}",
                        matchingId, bot.PlayerId, bot.CurrentArea, nextStep.Area, transitionDoor.DoorId);
                }

                return null;
            }
        }

        // Walk every waypoint at the same speed. An area transition is just the adjacent cell across a door.
        var targetPos = CellToWorldPosition(mapId, nextStep.Cell);
        float dx = targetPos.X - bot.Position.X;
        float dy = targetPos.Y - bot.Position.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float maxDist = BotWalkSpeed * GetBotMovementSpeedMultiplier(bot) * deltaSec;
        if (dist >= 0.01f)
            maxDist = ScaledWalkSpeed(dx / dist, dy / dist, GetBotMovementSpeedMultiplier(bot)) * deltaSec;

        Vector3f newPosition;
        Vector3f velocity;

        // 벽 판정 (2026-08-16 유저 제보: 봇이 문이 아니라 벽으로 넘어다닌다).
        // WalkStep은 웨이포인트로 직선 이동만 했다 — 경로가 한 칸이라도 어긋나면 그대로 통과한다.
        // 다음 웨이포인트가 비보행이면 그 경로는 이미 틀린 것이므로 버리고 다시 짠다.
        // 이미 벽 안에 서 있는 개체는 막지 않는다 — 막으면 영영 못 빠져나온다.
        if (!GameMapData.IsMoveablePosition(mapId, nextStep.Cell) &&
            GameMapData.IsMoveablePosition(mapId, bot.Cell))
        {
            bot.Path.Clear();
            bot.PathIndex = 0;
            bot.MovementDestination = AreaType.None;
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.4, 0.9);
            return null;
        }

        if (dist <= maxDist || dist < 0.01f)
        {
            // 도달 → 다음 인덱스
            newPosition = targetPos;
            bot.Cell = nextStep.Cell;
            bot.Position = newPosition;
            bot.PathIndex++;
            reachedStep = true;
            velocity = new Vector3f(0f, 0f, 0f);
            if (bot.PathIndex < bot.Path.Count)
            {
                var followingPos = CellToWorldPosition(mapId, bot.Path[bot.PathIndex].Cell);
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
                        bot.Position = newPosition;
                    }
                }
            }
        }
        else
        {
            float dirX = dx / dist;
            float dirY = dy / dist;
            newPosition = new Vector3f(
                bot.Position.X + dirX * maxDist,
                bot.Position.Y + dirY * maxDist,
                0f);
            velocity = ScaledWalkVelocity(dirX, dirY, GetBotMovementSpeedMultiplier(bot));
            bot.Position = newPosition;
        }

        bool areaChanged = false;
        if (reachedStep)
        {
            var resolvedArea = GameMapData.GetCurrentArea(mapId, bot.Cell);
            if (resolvedArea != AreaType.None && resolvedArea != bot.CurrentArea)
            {
                bot.CurrentArea = resolvedArea;
                areaChanged = true;
            }
        }

        bot.WalkVelocity = velocity;

        // velocity.X 부호에 따라 Rotation 갱신 (실제 플레이어 PlayerMovement.cs와 동일 규칙).
        // shouldFlip = velocity.X > 0 → rotation = 180 (오른쪽 보기), 아니면 0 (왼쪽 보기).
        // |velocity.X| < 0.1 시에는 직전 Rotation 유지(떨림 방지 — 클라 IsFlip 갱신 가드와 일치).
        if (velocity.X > 0.1f) bot.Rotation = 180f;
        else if (velocity.X < -0.1f) bot.Rotation = 0f;

        return new BotMovementEvent
        {
            BotPlayerId = bot.PlayerId,
            FromArea = fromArea,
            ToArea = bot.CurrentArea,
            FromCell = fromCell,
            ToCell = nextStep.Cell,
            Position = newPosition,
            Velocity = velocity,
            Rotation = bot.Rotation,
            IsAreaTransition = areaChanged
        };
    }

    /// <summary>
    ///     투사체 회피 (#232 §9, 2026-08-18 "제자리 좌우 와리가리 금지"): 위협이 있으면 리졸버가 준 방향으로
    ///     한 걸음 비켜서고 그 방향을 위협이 지나갈 때까지 커밋한다. 커밋 중에 띠 밖으로 나가 위협이 사라지면
    ///     원래 경로로 되돌아가지 않고 제자리에 선다 — 되돌아가면 다시 띠에 들어가 다시 비켜서는 떨림이 된다.
    ///     비켜선 칸이 벽이거나 다른 구역이면 반대쪽을 시도하고, 둘 다 막히면 회피 없이 원래 걸음.
    ///     경로·경로 인덱스는 손대지 않는다.
    /// </summary>
    /// <returns>true면 이번 틱은 회피 층이 처리했다(경로 걸음 없음). movement는 보낼 이벤트, 없으면 null.</returns>
    private bool TryDodgeStep(
        BotPlayerState bot, long matchingId, DateTime now, float deltaSec, out BotMovementEvent? movement)
    {
        movement = null;
        if (_swarmDodgeResolver == null || bot.CurrentArea == AreaType.None)
            return false;

        bool committed = now < bot.SwarmDodgeHoldUntilUtc;
        var advice = _swarmDodgeResolver(matchingId, bot.PlayerId, bot.Position, bot.CurrentArea, now);
        if (advice == null)
        {
            if (!committed)
                return false;
            // 띠 밖, 위협은 아직 안 지남 — 서서 기다린다. 걷기 패킷을 한 번 0으로 끊어 발소리·걷기 애니를 멈춘다.
            if (bot.WalkVelocity.X != 0f || bot.WalkVelocity.Y != 0f)
            {
                bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
                movement = new BotMovementEvent
                {
                    BotPlayerId = bot.PlayerId,
                    FromArea = bot.CurrentArea,
                    ToArea = bot.CurrentArea,
                    FromCell = bot.Cell,
                    ToCell = bot.Cell,
                    Position = bot.Position,
                    Velocity = bot.WalkVelocity,
                    Rotation = bot.Rotation,
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

        var mapId = GetMatchingMapId(matchingId);
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
            var candidate = new Vector3f(bot.Position.X + dirX * step, bot.Position.Y + dirY * step, 0f);
            var candidateCell = WorldToCell(candidate);
            if (!GameMapData.IsMoveablePosition(mapId, candidateCell) ||
                GameMapData.GetCurrentArea(mapId, candidateCell) != bot.CurrentArea)
                continue;

            var fromCell = bot.Cell;
            bot.Position = candidate;
            bot.Cell = candidateCell;
            var velocity = ScaledWalkVelocity(dirX, dirY, multiplier);
            bot.WalkVelocity = velocity;
            if (velocity.X > 0.1f) bot.Rotation = 180f;
            else if (velocity.X < -0.1f) bot.Rotation = 0f;
            movement = new BotMovementEvent
            {
                BotPlayerId = bot.PlayerId,
                FromArea = bot.CurrentArea,
                ToArea = bot.CurrentArea,
                FromCell = fromCell,
                ToCell = candidateCell,
                Position = candidate,
                Velocity = velocity,
                Rotation = bot.Rotation,
                IsAreaTransition = false
            };
            return true;
        }

        // 양쪽 다 막혔다 — 커밋을 풀고 원래 걸음으로 돌아간다.
        bot.SwarmDodgeHoldUntilUtc = DateTime.MinValue;
        return false;
    }

    /// <summary>
    ///     프로토 0: 봇이 도착했거나 경로가 비었을 때 다음 목적지(3·4층 방) 선택 + 경로 계산.
    ///     - 따라가기: 타겟(TargetPlayerId)이 있는 방으로 이동(회복).
    ///     - 떠보기: 일정 확률로 최저 인원 방으로 이동(추적자 유인).
    ///     복도는 목적지가 아니라 통과만(transit). 미션 수집 동선(직책 큐/RNG 채집)은 폐기.
    /// </summary>
    private void ChooseNewWanderTarget(BotPlayerState bot, long matchingId, AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager, InGameInventoryManager inventoryManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        IReadOnlyCollection<MonsterCombatTarget> pveTargets)
    {
        var mapId = GetMatchingMapId(matchingId);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;

        bool needsGuardianOrb = bot.EquippedBattleItemId <= 0;

        // 복도는 통로다. 잔상 분포로 목적지를 정할 수 있으면 그것이 우선이고,
        // 못 정할 때만 가장 가까운 방으로 나간다. 이전에는 복도 탈출이 먼저 걸려
        // 잔상 사냥 판단에 도달하지 못했고, 봇이 잔상 없는 방과 복도를 왕복했다
        // (2026-07-30 6판 계측: 봇 1인당 잔상 타격 29회 대 사람 122회).
        if (bot.CurrentArea.IsCorridor() && !needsGuardianOrb &&
            Config.MONSTER_SUMMON_ECONOMY_ENABLED &&
            TryStartAfterimageHuntPath(bot, matchingId, mapId, closureManager, inventoryManager, pveTargets))
        {
            return;
        }

        if (bot.CurrentArea.IsCorridor() &&
            TryStartCorridorExitPath(bot, matchingId, mapId, closureManager))
        {
            return;
        }

        if (!needsGuardianOrb && bot.IsForcedFollowActive &&
            TryStartBotForcedFollowPath(bot, matchingId, mapId, playerAreas))
        {
            return;
        }

        // Starting orbs are granted before the first movement tick. Keep the guard for an
        // unexpected initialization failure, but never route to RNG pickup locations.
        if (needsGuardianOrb)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.6);
            return;
        }

        if (Config.MONSTER_SUMMON_ECONOMY_ENABLED &&
            TryStartAfterimageHuntPath(bot, matchingId, mapId, closureManager, inventoryManager, pveTargets))
        {
            return;
        }

        var destination = ChooseBehaviorDestination(bot, matchingId, mapId, playerAreas, closureManager);
        if (destination == AreaType.None) return;
        if (destination == bot.CurrentArea)
        {
            // 이미 원하는 방(타겟 방 등)에 있음 → 잠시 머물며 회복/기척.
            // (즉시 재결정 시 떠보기 확률이 매 틱 굴러 곧바로 나가버리는 문제 방지)
            bot.LoopWaitUntil = RandomizedDelayFromNow(Proto0RoomDwellMinSeconds, Proto0RoomDwellMaxSeconds);
            return;
        }

        var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, destination)
            ?? GameMapData.GetAreaSpawnCell(mapId, destination);

        var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
            destination, targetCell,
            a => IsAreaClosingOrClosed(closureManager, matchingId, a));
        if (path == null || path.Count == 0)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.6);
            _logger.LogDebug("프로토0 봇 경로 실패: BotId={Bot}, {From} → {To}",
                bot.PlayerId, bot.CurrentArea, destination);
            return;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.MovementDestination = destination;
        bot.LoopWaitUntil = RandomizedDelayFromNow(0.25, 0.6);
        _logger.LogInformation(
            "Proto0 bot move: BotId={Bot}, Target={Target}, Policy={Policy}, Profile={Profile}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.TargetPlayerId, ActiveProto0BotPolicy, bot.Proto0Profile,
            bot.CurrentArea, destination, path.Count);
    }


    /// <summary>
    /// Survivor Royale PVE policy: treat an afterimage pack as the room objective.
    /// The score deliberately favors a visible core and an under-contested pack, while
    /// retaining one committed destination until arrival so door thresholds cannot flip
    /// the bot between two adjacent rooms every movement tick.
    /// </summary>
    private bool TryStartAfterimageHuntPath(
        BotPlayerState bot,
        long matchingId,
        MapId mapId,
        AreaClosureManager closureManager,
        InGameInventoryManager inventoryManager,
        IReadOnlyCollection<MonsterCombatTarget> pveTargets)
    {
        // 복도에서도 목적지를 고를 수 있어야 한다. 복도에 서 있는 봇에게는 방만 후보로
        // 남으므로 "현재 지역 사냥" 분기를 타지 않고 경로만 받는다.
        if (bot.CurrentArea == AreaType.None || pveTargets.Count == 0)
            return false;

        var closure = closureManager.GetClientStateSnapshot(matchingId);
        var unavailable = closure.ClosedAreas.Concat(closure.WarningAreas).ToHashSet();
        var boardItemIds = inventoryManager.GetPlayerInventory(matchingId, bot.PlayerId)
            .GetAllItems()
            .Select(item => item.ItemId)
            .ToArray();
        // 복도 잔상도 목적지가 된다. 폐쇄 페이즈마다 보상이 2배로 커지는데 봇만 방에
        // 묶여 있으면, 사람만 아는 복도 파밍이 그대로 격차가 된다 (2026-07-31 match-2015:
        // 사람 소환석 425 중 412가 복도, 봇 최고치는 123 중 20).
        // 다만 이미 복도에 서 있는 봇에게 복도를 다시 목적지로 주면 제자리에서 맴돈다.
        // 그 경우는 자동전투가 근처 잔상을 알아서 잡으므로 목적지에서만 뺀다.
        var areaGroups = pveTargets
            .Where(target => target.MapId == mapId &&
                             target.Area != AreaType.None &&
                             !(target.Area.IsCorridor() && bot.CurrentArea.IsCorridor()) &&
                             !unavailable.Contains(target.Area))
            .GroupBy(target => target.Area)
            .ToList();

        // 경로 탐색이 이 틱의 비용을 지배한다. 잔상이 있는 모든 지역에 길을 찾으면
        // 봇 수 x 지역 수만큼 A*가 돌아 틱이 200~370ms까지 튀고, 그동안 이동 틱이
        // 통째로 스킵되어 봇 위치 브로드캐스트가 끊긴다 (2026-07-31 계측: 200틱 중 93틱 스킵).
        //
        // 현재 지역과 인접 지역만 후보로 둔다. 미니맵이 잔상 분포를 공개하므로 봇이 그
        // 정보를 쓰는 것 자체는 규칙에 맞지만, 맵 반대편까지 직행할 필요는 없다. 인접
        // 이동을 반복하면 결국 도달하고, 가까운 곳부터 훑는 편이 사람의 판단에 가깝다.
        var nearbyGroups = areaGroups
            .Where(group => group.Key == bot.CurrentArea ||
                            GameAreaConnectionData.IsAdjacent(mapId, bot.CurrentArea, group.Key))
            .ToList();
        // 인접한 곳에 잔상이 하나도 없을 때만 전체 지역으로 넓힌다.
        if (nearbyGroups.Count > 0)
            areaGroups = nearbyGroups;

        var candidates = areaGroups
            .Select(group =>
            {
                var preferredTarget = group
                    .OrderByDescending(target => target.IsCore)
                    .ThenBy(target => DistanceSquared(bot.Position, target.Position.X, target.Position.Y))
                    .First();
                var targetCell = WorldToCell(preferredTarget.Position);
                var pathTargetCell = group.Key == bot.CurrentArea
                    ? targetCell
                    : GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, group.Key) ?? targetCell;
                int coreCount = group.Count(target => target.IsCore);
                float affinityScore = CalculateBotPveAffinityScore(boardItemIds, preferredTarget.RewardItemId);
                int estimatedSteps = Math.Abs(bot.Cell.X - pathTargetCell.X) +
                                     Math.Abs(bot.Cell.Y - pathTargetCell.Y);
                return new
                {
                    Area = group.Key,
                    Target = preferredTarget,
                    PathTargetCell = pathTargetCell,
                    AffinityScore = affinityScore,
                    Score = group.Count() * 3 + coreCount * 8 + affinityScore * 4 -
                            CountAreaPressure(matchingId, group.Key, bot.PlayerId) * 5,
                    EstimatedSteps = estimatedSteps
                };
            })
            .OrderByDescending(candidate => candidate.Area == bot.CurrentArea)
            .ThenByDescending(candidate => candidate.Score - candidate.EstimatedSteps * 0.2)
            .ThenBy(candidate => candidate.Area == bot.CurrentArea ? 0 : 1)
            .ThenBy(candidate => Math.Abs((int)(bot.PlayerId % 97) - (int)candidate.Area))
            .Take(MaxHuntPathCandidates)
            .ToList();

        int selectedIndex = -1;
        List<BotPathfinder.Step>? selectedPath = null;
        for (int index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var path = candidate.Area == bot.CurrentArea
                ? BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell, bot.CurrentArea,
                    candidate.PathTargetCell,
                    area => area != bot.CurrentArea || unavailable.Contains(area))
                : BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell, candidate.Area,
                    candidate.PathTargetCell,
                    unavailable.Contains);
            if (path is not { Count: > 0 } && candidate.Area != bot.CurrentArea)
                continue;

            selectedIndex = index;
            selectedPath = path;
            break;
        }

        var selected = selectedIndex >= 0 ? candidates[selectedIndex] : null;

        // 정체 판정이 실제로 봇을 내보냈는지 확인할 수 있어야 한다. 탈출에 실패하면
        // 후보가 없는 것인지 경로를 못 찾은 것인지 이 한 줄로 갈린다.
        // 타이머를 여기서 다시 세워 같은 봇이 매 틱 로그를 쏟지 않게 한다.
        if (selected == null)
            return false;

        // The bot already owns this room's hunt. Let the automatic combat and lateral
        // kite logic work instead of immediately replacing the local objective.
        if (selected.Area == bot.CurrentArea && selectedPath is not { Count: > 0 })
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.45, 0.9);
            return true;
        }

        bot.Path = selectedPath!;
        bot.PathIndex = 0;
        bot.MovementDestination = selected.Area;
        bot.LoopWaitUntil = DateTime.MinValue;
        _logger.LogDebug(
            "Bot afterimage hunt route: MatchingId={MatchingId}, BotId={BotId}, {From}->{To}, PackMembers={PackMembers}, Core={Core}, Affinity={Affinity}, Steps={Steps}",
            matchingId,
            bot.PlayerId,
            bot.CurrentArea,
            selected.Area,
            pveTargets.Count(target => target.Area == selected.Area),
            selected.Target.IsCore,
            selected.AffinityScore,
            selectedPath!.Count);
        return true;
    }

    private static float CalculateBotPveAffinityScore(IEnumerable<int> boardItemIds, int monsterRewardItemId)
    {
        float score = 0f;
        foreach (int itemId in boardItemIds)
        {
            if (OrbData.TryGetColorAndTier(itemId, out OrbColor color, out int tier))
            {
                score += tier * OrbData.GetPveDamageMultiplier(color, monsterRewardItemId);
                continue;
            }

            if (OrbData.TryGetRecoveryTier(itemId, out int recoveryTier))
                score += recoveryTier * 0.25f;
        }

        return score;
    }

    private bool TryStartBotForcedFollowPath(BotPlayerState bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        if (!playerAreas.TryGetValue(bot.TargetPlayerId, out var targetArea) || targetArea == AreaType.None)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.4, 0.9);
            return true;
        }

        if (targetArea == bot.CurrentArea)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.5);
            return true;
        }

        var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, targetArea)
            ?? GameMapData.GetAreaSpawnCell(mapId, targetArea);

        var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
            targetArea, targetCell,
            _ => false);
        if (path == null || path.Count == 0)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.4, 0.9);
            _logger.LogWarning(
                "Bot forced follow path failed: MatchingId={MatchingId}, BotId={Bot}, Target={Target}, {From}->{To}",
                matchingId, bot.PlayerId, bot.TargetPlayerId, bot.CurrentArea, targetArea);
            return true;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.MovementDestination = targetArea;
        bot.LoopWaitUntil = RandomizedDelayFromNow(0.3, 0.8);
        _logger.LogInformation(
            "Bot forced follow move: MatchingId={MatchingId}, BotId={Bot}, Target={Target}, {From}->{To}, Steps={Steps}",
            matchingId, bot.PlayerId, bot.TargetPlayerId, bot.CurrentArea, targetArea, path.Count);
        return true;
    }

    /// <summary>
    ///     프로토 0 목적지(방) 선택. 떠보기 확률이면 최저 인원 방, 아니면 타겟이 있는 방(회복).
    ///     타겟 위치를 모르면 현재와 다른 임의 방.
    /// </summary>
    private AreaType ChooseProto0Destination(BotPlayerState bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        return ActiveProto0BotPolicy switch
        {
            Proto0BotPolicy.DisguiseMvp => ChooseDisguiseProto0Destination(bot, matchingId, mapId, playerAreas, closureManager),
            _ => ChooseSimpleProto0Destination(bot, matchingId, mapId, playerAreas, closureManager)
        };
    }

    private bool TryStartCorridorExitPath(BotPlayerState bot, long matchingId, MapId mapId,
        AreaClosureManager closureManager)
    {
        var exitAreas = GetOpenBotDestinationAreas(matchingId, mapId, closureManager)
            .Where(area => area != bot.CurrentArea)
            .ToList();

        // 복도에서는 인접한 방으로 나가면 충분하다. 열린 지역 전체에 길을 찾으면 봇마다
        // A*가 지역 수만큼 돌아 이동 틱이 200ms 넘게 튀고, 그 사이 틱이 스킵되어 봇 위치
        // 브로드캐스트가 끊긴다. 인접한 곳이 없을 때만 전체로 넓힌다.
        var adjacentExitAreas = exitAreas
            .Where(area => GameAreaConnectionData.IsAdjacent(mapId, bot.CurrentArea, area))
            .ToList();
        if (adjacentExitAreas.Count > 0)
            exitAreas = adjacentExitAreas;

        var exit = exitAreas
            .Select(area => new
            {
                Area = area,
                Path = BotPathfinder.FindPath(
                    mapId,
                    bot.CurrentArea,
                    bot.Cell,
                    area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, area)
                    ?? GameMapData.GetAreaSpawnCell(mapId, area),
                    candidate => IsAreaClosingOrClosed(closureManager, matchingId, candidate))
            })
            .Where(candidate => candidate.Path is { Count: > 0 })
            .OrderBy(candidate => candidate.Path!.Count)
            .ThenBy(candidate => CountAreaPressure(matchingId, candidate.Area))
            .ThenBy(candidate => (int)candidate.Area)
            .FirstOrDefault();

        if (exit?.Path == null)
            return false;

        bot.Path = exit.Path;
        bot.PathIndex = 0;
        bot.MovementDestination = exit.Area;
        bot.LoopWaitUntil = DateTime.MinValue;
        _logger.LogDebug(
            "Bot corridor exit: BotId={Bot}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.CurrentArea, exit.Area, exit.Path.Count);
        return true;
    }

    private AreaType ChooseBehaviorDestination(BotPlayerState bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        var players = playerAreas
            .Select(p => new BotBehaviorPlayerSnapshot
            {
                PlayerId = p.Key,
                CurrentArea = p.Value,
                TargetPlayerId = 0,
                IsEliminated = false
            })
            .ToList();

        var decision = BotBehaviorDecisionService.Decide(
            bot,
            players,
            area => IsAreaClosingOrClosed(closureManager, matchingId, area));

        // Corridors are transit only. Following a target whose current area is a corridor
        // must fall through to the room-selection policy; otherwise bots path to the
        // corridor center and have no room destination to continue toward.
        if (decision.Kind == BotBehaviorActionKind.FollowTarget
            && decision.TargetArea != AreaType.None
            && !decision.TargetArea.IsCorridor()
            && decision.TargetArea != bot.CurrentArea)
            return decision.TargetArea;

        return ChooseProto0Destination(bot, matchingId, mapId, playerAreas, closureManager);
    }


    private List<AreaType> GetOpenBotDestinationAreas(long matchingId, MapId mapId,
        AreaClosureManager closureManager)
    {
        return GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() &&
                           !IsAreaClosingOrClosed(closureManager, matchingId, area))
            .ToList();
    }

    private AreaType ChooseSimpleProto0Destination(BotPlayerState bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        var rooms = GetOpenBotDestinationAreas(matchingId, mapId, closureManager);
        if (rooms.Count == 0) return AreaType.None;

        // 떠보기: 최저 인원 방으로 (추적자 유인 — 회복 포기 비용)
        if (_rng.NextDouble() < Proto0TestProbability)
        {
            var pop = CountRoomPopulations(rooms, playerAreas);
            return rooms.OrderBy(a => pop[a]).ThenBy(_ => _rng.Next()).First();
        }

        // 따라가기: 타겟이 있는 방으로 (회복)
        if (playerAreas.TryGetValue(bot.TargetPlayerId, out var targetArea) && targetArea != AreaType.None)
        {
            if (!targetArea.IsCorridor() && rooms.Contains(targetArea)) return targetArea;
            // 타겟이 복도면 같은 층 방으로
            int floor = targetArea.GetFloor();
            var floorRooms = rooms.Where(a => a.GetFloor() == floor).ToList();
            if (floorRooms.Count > 0) return floorRooms[_rng.Next(floorRooms.Count)];
        }

        // 타겟 위치 불명 → 현재와 다른 임의 방
        var others = rooms.Where(a => a != bot.CurrentArea).ToList();
        return others.Count > 0 ? others[_rng.Next(others.Count)] : AreaType.None;
    }

    /// <summary>프로토 0 위장 정책: 즉시 추적 대신 지연, 미끼 이동, 떠보기 이동을 섞는다.</summary>
    private AreaType ChooseDisguiseProto0Destination(BotPlayerState bot, long matchingId, MapId mapId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        var rooms = GetOpenBotDestinationAreas(matchingId, mapId, closureManager);
        if (rooms.Count == 0) return AreaType.None;

        var now = DateTime.UtcNow;
        var pop = CountRoomPopulations(rooms, playerAreas);
        var config = GetProto0ProfileConfig(bot.Proto0Profile);
        var targetRoom = ResolveTargetRoom(rooms, playerAreas, bot.TargetPlayerId);

        if (targetRoom != AreaType.None && targetRoom != bot.LastSeenTargetArea)
        {
            bot.LastSeenTargetArea = targetRoom;
            var delaySeconds = RandomRange(Proto0FollowDelayMinSeconds, Proto0FollowDelayMaxSeconds)
                * config.FollowDelayMultiplier;
            bot.NextTargetFollowAllowedAt = now.AddSeconds(delaySeconds);
        }
        else if (targetRoom == AreaType.None)
        {
            bot.LastSeenTargetArea = AreaType.None;
            bot.NextTargetFollowAllowedAt = DateTime.MinValue;
        }

        var canFakeMove = bot.Stamina >= config.FakeMoveMinStamina
            && (now - bot.LastFakeMoveTime).TotalSeconds >= Proto0FakeMoveCooldownSeconds;
        var canProbe = (now - bot.LastProbeMoveTime).TotalSeconds >= Proto0ProbeCooldownSeconds;

        if (canFakeMove && targetRoom != AreaType.None && now < bot.NextTargetFollowAllowedAt
            && TryChooseFakeRoom(bot, rooms, pop, targetRoom, config, out var delayedRoom))
        {
            bot.LastFakeMoveTime = now;
            return delayedRoom;
        }

        var currentIsCrowded = rooms.Contains(bot.CurrentArea)
            && pop[bot.CurrentArea] >= Proto0CrowdedRoomThreshold;
        if (canFakeMove && currentIsCrowded && !config.PrefersCrowd
            && _rng.NextDouble() < config.FakeMoveChance
            && TryChooseFakeRoom(bot, rooms, pop, targetRoom, config, out var crowdExitRoom))
        {
            bot.LastFakeMoveTime = now;
            return crowdExitRoom;
        }

        var targetIsPrivate = targetRoom != AreaType.None && pop[targetRoom] <= 1 && targetRoom != bot.CurrentArea;
        if (canFakeMove && targetIsPrivate && config.AvoidsPrivateTarget
            && _rng.NextDouble() < config.FakeMoveChance
            && TryChooseFakeRoom(bot, rooms, pop, targetRoom, config, out var decoyRoom))
        {
            bot.LastFakeMoveTime = now;
            return decoyRoom;
        }

        if (canProbe && _rng.NextDouble() < config.ProbeChance
            && TryChooseProbeRoom(bot, rooms, pop, out var probeRoom))
        {
            bot.LastProbeMoveTime = now;
            return probeRoom;
        }

        if (canFakeMove && _rng.NextDouble() < config.FakeMoveChance
            && TryChooseFakeRoom(bot, rooms, pop, targetRoom, config, out var fakeRoom))
        {
            bot.LastFakeMoveTime = now;
            return fakeRoom;
        }

        if (targetRoom != AreaType.None) return targetRoom;
        return ChooseProfileFallbackRoom(bot, rooms, pop, config);
    }

    private AreaType ResolveTargetRoom(List<AreaType> rooms, IReadOnlyDictionary<long, AreaType> playerAreas,
        long targetPlayerId)
    {
        if (!playerAreas.TryGetValue(targetPlayerId, out var targetArea) || targetArea == AreaType.None)
            return AreaType.None;

        if (!targetArea.IsCorridor() && rooms.Contains(targetArea)) return targetArea;

        var floor = targetArea.GetFloor();
        var floorRooms = rooms.Where(a => a.GetFloor() == floor).ToList();
        return floorRooms.Count > 0 ? floorRooms[_rng.Next(floorRooms.Count)] : AreaType.None;
    }

    private bool TryChooseFakeRoom(BotPlayerState bot, List<AreaType> rooms, Dictionary<AreaType, int> pop,
        AreaType targetRoom, Proto0ProfileConfig config, out AreaType room)
    {
        var candidates = rooms
            .Where(a => a != targetRoom && a != bot.CurrentArea)
            .ToList();

        if (candidates.Count == 0)
            candidates = rooms.Where(a => a != targetRoom).ToList();

        if (candidates.Count == 0)
        {
            room = AreaType.None;
            return false;
        }

        room = ChooseProfileRoom(candidates, pop, config);
        return true;
    }

    private bool TryChooseProbeRoom(BotPlayerState bot, List<AreaType> rooms, Dictionary<AreaType, int> pop,
        out AreaType room)
    {
        var candidates = rooms.Where(a => a != bot.CurrentArea).ToList();
        if (candidates.Count == 0)
        {
            room = AreaType.None;
            return false;
        }

        room = candidates
            .OrderBy(a => pop[a])
            .ThenBy(_ => _rng.Next())
            .First();
        return true;
    }

    private AreaType ChooseProfileFallbackRoom(BotPlayerState bot, List<AreaType> rooms,
        Dictionary<AreaType, int> pop, Proto0ProfileConfig config)
    {
        var candidates = rooms.Where(a => a != bot.CurrentArea).ToList();
        return candidates.Count > 0
            ? ChooseProfileRoom(candidates, pop, config)
            : rooms[_rng.Next(rooms.Count)];
    }

    private AreaType ChooseProfileRoom(List<AreaType> rooms, Dictionary<AreaType, int> pop,
        Proto0ProfileConfig config)
    {
        if (config.PrefersCrowd)
            return rooms.OrderByDescending(a => pop[a]).ThenBy(_ => _rng.Next()).First();

        if (config.PrefersQuiet)
            return rooms.OrderBy(a => pop[a]).ThenBy(_ => _rng.Next()).First();

        return rooms
            .OrderBy(a => Math.Abs(pop[a] - 2))
            .ThenBy(_ => _rng.Next())
            .First();
    }

    private double RandomRange(double min, double max)
    {
        return min + _rng.NextDouble() * (max - min);
    }

    private DateTime RandomizedDelayFromNow(double minSeconds, double maxSeconds)
    {
        return DateTime.UtcNow.AddSeconds(RandomRange(minSeconds, maxSeconds));
    }

    private static Proto0ProfileConfig GetProto0ProfileConfig(BotProto0Profile profile)
    {
        return profile switch
        {
            BotProto0Profile.StealthFirst => new Proto0ProfileConfig(
                probeChance: 0.16,
                fakeMoveChance: 0.45,
                followDelayMultiplier: 1.35,
                fakeMoveMinStamina: 55,
                prefersCrowd: false,
                prefersQuiet: false,
                avoidsPrivateTarget: true),
            BotProto0Profile.AggressiveProbe => new Proto0ProfileConfig(
                probeChance: 0.42,
                fakeMoveChance: 0.24,
                followDelayMultiplier: 0.95,
                fakeMoveMinStamina: 60,
                prefersCrowd: false,
                prefersQuiet: true,
                avoidsPrivateTarget: false),
            BotProto0Profile.CrowdSeeking => new Proto0ProfileConfig(
                probeChance: 0.12,
                fakeMoveChance: 0.26,
                followDelayMultiplier: 1.1,
                fakeMoveMinStamina: 60,
                prefersCrowd: true,
                prefersQuiet: false,
                avoidsPrivateTarget: false),
            BotProto0Profile.QuietRoomSeeking => new Proto0ProfileConfig(
                probeChance: 0.30,
                fakeMoveChance: 0.30,
                followDelayMultiplier: 1.15,
                fakeMoveMinStamina: 65,
                prefersCrowd: false,
                prefersQuiet: true,
                avoidsPrivateTarget: true),
            _ => new Proto0ProfileConfig(
                probeChance: 0.10,
                fakeMoveChance: 0.12,
                followDelayMultiplier: 0.7,
                fakeMoveMinStamina: 75,
                prefersCrowd: false,
                prefersQuiet: false,
                avoidsPrivateTarget: false)
        };
    }

    private readonly struct Proto0ProfileConfig
    {
        public Proto0ProfileConfig(double probeChance, double fakeMoveChance, double followDelayMultiplier,
            int fakeMoveMinStamina, bool prefersCrowd, bool prefersQuiet, bool avoidsPrivateTarget)
        {
            ProbeChance = probeChance;
            FakeMoveChance = fakeMoveChance;
            FollowDelayMultiplier = followDelayMultiplier;
            FakeMoveMinStamina = fakeMoveMinStamina;
            PrefersCrowd = prefersCrowd;
            PrefersQuiet = prefersQuiet;
            AvoidsPrivateTarget = avoidsPrivateTarget;
        }

        public double ProbeChance { get; }
        public double FakeMoveChance { get; }
        public double FollowDelayMultiplier { get; }
        public int FakeMoveMinStamina { get; }
        public bool PrefersCrowd { get; }
        public bool PrefersQuiet { get; }
        public bool AvoidsPrivateTarget { get; }
    }

    /// <summary>프로토 0: 각 방의 현재 인원수(봇+인간) 집계. 떠보기 목적지 선택용.</summary>
    private static Dictionary<AreaType, int> CountRoomPopulations(List<AreaType> rooms,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        var pop = rooms.ToDictionary(a => a, _ => 0);
        foreach (var area in playerAreas.Values)
            if (pop.ContainsKey(area)) pop[area]++;
        return pop;
    }

    private bool TryStartBotInteractPath(BotPlayerState bot, long matchingId, AreaType area, int interactId,
        AreaClosureManager closureManager)
    {
        if (bot.IsEliminated) return false;
        if (IsAreaClosingOrClosed(closureManager, matchingId, area)) return false;

        var info = GameInteractableData.Get(interactId);
        if (info == null || info.ZoneId != (int)area) return false;
        if (info.CellX == 0 && info.CellY == 0) return false;

        var mapId = GetMatchingMapId(matchingId);
        var targetCell = new Cell(info.CellX, info.CellY);
        if (bot.CurrentArea == area && bot.Cell.Equals(targetCell))
        {
            bot.Path.Clear();
            bot.PathIndex = 0;
            bot.MovementDestination = AreaType.None;
            bot.PendingRngInteractId = interactId;
            bot.RngCollectProgressStartTime = DateTime.MinValue;
            bot.IsChannelHeld = false;
            bot.ChannelHoldUntil = DateTime.MinValue;
            bot.PendingForcedInteractArea = AreaType.None;
            bot.PendingForcedInteractId = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.WalkVelocity = new Vector3f(0f, 0f, 0f);

            _logger.LogInformation(
                "Bot gift pickup queued at current cell: BotId={Bot}, Area={Area}, InteractId={InteractId}",
                bot.PlayerId, area, interactId);
            return true;
        }

        var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
            area, targetCell,
            a => IsAreaClosingOrClosed(closureManager, matchingId, a));
        if (path == null || path.Count == 0) return false;

        bot.Path = path;
        bot.PathIndex = 0;
        bot.MovementDestination = area;
        bot.PendingRngInteractId = interactId;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        bot.IsChannelHeld = false;
        bot.ChannelHoldUntil = DateTime.MinValue;
        bot.PendingForcedInteractArea = AreaType.None;
        bot.PendingForcedInteractId = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        _logger.LogInformation(
            "봇 선물 회수 이동 시작: BotId={Bot}, Area={Area}, InteractId={InteractId}, Steps={Steps}",
            bot.PlayerId, area, interactId, path.Count);
        return true;
    }

    /// <summary>
    ///     W3 시연 모드 — 봇 위치를 BotMovementScript에 따라 강제. 매 틱(5초)마다 평가.
    ///     큐 순회 로직 우회. 폐쇄된 위치는 도착 보류(다음 웨이포인트로 진행되면 자연 해소).
    /// </summary>
    /// <summary>
    ///     봇 영역 전환 — Cell/Position을 새 영역의 스폰 셀로 갱신하고 BotMovementEvent 생성.
    ///     legacy mode 스크립트 텔레포트 전용. walking 경로 통과 시점은 WalkStep에서 처리.
    /// </summary>
}

/// <summary>
///     봇 이동 이벤트. ProcessBotTick / ProcessBotMovementTick이 반환하면 GameServer가 같은 영역 인간 세션에 패킷 브로드캐스트.
///     영역 변경 시: 보행으로 새 영역 셀에 도착한 뒤 LEAVE + ENTER + MOVE를 전송.
///     영역 내 walk 시: G_TO_C_MOVE 만 (같은 영역 인간들에게)
/// </summary>
public class BotMovementEvent
{
    public long BotPlayerId { get; set; }
    public AreaType FromArea { get; set; }
    public AreaType ToArea { get; set; }
    public Cell FromCell { get; set; } = new(0, 0);
    public Cell ToCell { get; set; } = new(0, 0);
    public Vector3f Position { get; set; } = new(0f, 0f, 0f);
    public Vector3f Velocity { get; set; } = new(0f, 0f, 0f);
    public float Rotation { get; set; }
    public bool IsAreaTransition { get; set; }
}
