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
    private const double RoomHuntStallSeconds = 40;
    private const float PveKiteThreatRange = 3.2f;
    private const float PveKiteImmediateThreatRange = 1.65f;
    private const float PveKiteStepDistance = 2.15f;

    /// <summary>아이소메트릭 세로 속도 보정 — 클라 PlayerMovement.isoVerticalSpeedScale과 같은 값을 유지해야 한다.</summary>
    private const float IsoVerticalSpeedScale = 1f;

    // Used only after the final room closure leaves no non-corridor refuge.
    private static readonly (int X, int Y)[] LastStandPatrolOffsets =
    [
        (4, 0), (0, 4), (-4, 0), (0, -4),
        (3, 2), (-3, 2), (3, -2), (-3, -2)
    ];

    /// <summary>
    ///     화면 좌표 진행 방향(정규화)의 타일 기준 등속 속력.
    ///     세로가 압축된 아이소메트릭 화면에서 어느 방향이든 타일 통과 속도가 BotWalkSpeed로 일정해진다
    ///     (플레이어 로컬 이동의 세로 보정과 동일 규칙).
    /// </summary>
    private static float ScaledWalkSpeed(float dirX, float dirY, bool windResonanceActive = false)
    {
        float tileY = dirY / IsoVerticalSpeedScale;
        float tileFactor = (float)Math.Sqrt(dirX * dirX + tileY * tileY);
        float baseSpeed = BotWalkSpeed * (windResonanceActive ? SurvivorOrbData.WindMoveSpeedMultiplier : 1f);
        return tileFactor > 0.0001f ? baseSpeed / tileFactor : baseSpeed;
    }

    private static float GetBotMovementSpeedMultiplier(BotPlayerState bot)
    {
        float wind = bot.WindResonanceActive ? SurvivorOrbData.WindMoveSpeedMultiplier : 1f;
        return wind * GetBotWaveSlowMultiplier(bot);
    }

    private static float GetBotWaveSlowMultiplier(BotPlayerState bot)
    {
        return DateTime.UtcNow < bot.WaveSlowUntilUtc
            ? SurvivorOrbData.WaveSlowMoveSpeedMultiplier
            : 1f;
    }
    private static Vector3f ScaledWalkVelocity(float dirX, float dirY, bool windResonanceActive = false)
    {
        float speed = ScaledWalkSpeed(dirX, dirY, windResonanceActive);
        return new Vector3f(dirX * speed, dirY * speed, 0f);
    }


    /// <summary>봇 자원 틱 결과. 자원 고갈 탈락 + 위치 이동 이벤트(legacy mode 영역 전환만)를 함께 반환.</summary>
    public class BotTickResult
    {
        public List<(long botPlayerId, EliminationReason reason)> Eliminated { get; } = new();
        public List<BotMovementEvent> Movements { get; } = new();
    }

    /// <summary>#134 봇 walking 틱 결과 — Movements + ExploreEnds (walking 시작 시 EXPLORE_END broadcast 안전망).</summary>
    public class BotWalkingTickResult
    {
        public List<BotMovementEvent> Movements { get; } = new();
        public List<(long botId, AreaType area)> ExploreEnds { get; } = new();
        public List<BotGroundItemPickup> GroundItemPickups { get; } = new();
        public long PlanningBotId { get; set; }
        public double PlanningElapsedMilliseconds { get; set; }
        public double WalkingElapsedMilliseconds { get; set; }
    }

    /// <summary>
    ///     봇 자원 틱(5초). 자원 변동 + 탈락 + legacy mode 스크립트 영역 전환만 처리.
    ///     일반 walking은 ProcessBotMovementTick(250ms)에서 별도 처리.
    /// </summary>
    public BotTickResult ProcessBotTick(
        long matchingId,
        int resourceTickSeconds,
        AreaClosureManager areaClosureManager)
    {
        var result = new BotTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            int totalCorruptionDelta = areaClosureManager.GetEnvironmentalCorruptionDelta(
                matchingId,
                bot.CurrentArea,
                resourceTickSeconds);

            if (totalCorruptionDelta != 0)
                bot.Corruption = Math.Clamp(bot.Corruption + totalCorruptionDelta, 0, Config.SURVIVOR_MAX_CORRUPTION);

            if (TryQueueBotMentalElimination(bot, matchingId, result))
                continue;

            // legacy mode 비활성에서도 일반 자원/상태 변동 후 오염도 100이면 탈락 처리한다.
        }
        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;
            SyncBotForcedFollowState(bot, matchingId);
        }

        return result;
    }

    public void ApplyEnvironmentalCorruption(BotPlayerState bot, int corruptionDelta)
    {
        if (bot.IsEliminated || corruptionDelta == 0)
            return;

        bot.Corruption = Math.Clamp(bot.Corruption + corruptionDelta, 0, Config.SURVIVOR_MAX_CORRUPTION);
    }

    public void MarkEnvironmentalEliminated(BotPlayerState bot, long matchingId)
    {
        if (bot.IsEliminated)
            return;

        bot.IsEliminated = true;
        bot.IsForcedFollowActive = false;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        ClearBotRoomExplorePlan(bot);
        bot.LoopWaitUntil = DateTime.MinValue;

        _logger.LogInformation(
            "Bot environmental elimination finalized: MatchingId={MatchingId}, BotId={BotId}, Corruption={Corruption}",
            matchingId, bot.PlayerId, bot.Corruption);
    }
    private bool TryQueueBotMentalElimination(BotPlayerState bot, long matchingId, BotTickResult result)
    {
        if (bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION) return false;

        bot.IsEliminated = true;
        bot.IsForcedFollowActive = false;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        ClearBotRoomExplorePlan(bot);
        bot.LoopWaitUntil = DateTime.MinValue;
        result.Eliminated.Add((bot.PlayerId, EliminationReason.MENTAL_ZERO));

        _logger.LogInformation(
            "Bot mental depleted: MatchingId={MatchingId}, BotId={BotId}, Corruption={Corruption}. Eliminating bot.",
            matchingId, bot.PlayerId, bot.Corruption);
        return true;
    }

    private void SyncBotForcedFollowState(BotPlayerState bot, long matchingId)
    {
        if (!bot.IsForcedFollowActive) return;

        bot.IsForcedFollowActive = false;
        _logger.LogInformation(
            "Bot forced follow ended: MatchingId={MatchingId}, BotId={BotId}, Corruption={Corruption}",
            matchingId, bot.PlayerId, bot.Corruption);
    }

    /// <summary>
    ///     #127: 봇 walking 틱(50ms). legacy mode 비활성 시 BotPathfinder 경로를 따라 셀 단위 이동.
    ///     Uses the same fixed movement speed 6.0 as the player and emits an equivalent G_TO_C_MOVE event each tick.
    ///     #134: 추가로 ChooseNewWanderTarget 시 PendingExploreEndBroadcast가 set된 봇은 ExploreEnds list에 수집 — walking 시작 안전망.
    /// </summary>
    public BotWalkingTickResult ProcessBotMovementTick(long matchingId, AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager,
        IReadOnlyDictionary<long, AreaType> humanAreas,
        ChecklistManager checklistManager,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        IReadOnlyCollection<BotCombatTargetSnapshot> combatTargets,
        IReadOnlyCollection<MonsterCombatTarget>? pveTargets = null,
        SurvivorPhaseManager? survivorPhaseManager = null)
    {
        var result = new BotWalkingTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        var activeBots = bots.Where(bot => !bot.IsEliminated).ToList();
        if (activeBots.Count == 0) return result;

        result.PlanningBotId = SelectMovementPlanningBot(matchingId, activeBots);

        // 전체 플레이어(인간 + 봇) 현재 영역 맵 — 봇 타겟 추적/떠보기 인원수 계산용.
        var playerAreas = new Dictionary<long, AreaType>(humanAreas);
        foreach (var b in activeBots)
            playerAreas[b.PlayerId] = b.CurrentArea;

        SurvivorPhaseSnapshot survivorPhase = survivorPhaseManager?.GetSnapshot(matchingId)
            ?? SurvivorPhaseSnapshot.Empty;
        bool hasOpenNonCorridorRefuge = HasOpenNonCorridorRefuge(matchingId, closureManager);
        foreach (var bot in activeBots)
        {
            bool canPlanThisTick = bot.PlayerId == result.PlanningBotId;
            bool isEvacuating = bot.EvacuationDestination != AreaType.None &&
                                bot.PathIndex < bot.Path.Count;
            bool isCommittingToDestination = bot.MovementDestination != AreaType.None &&
                                             bot.PathIndex < bot.Path.Count;

            long planningStartedAt = Stopwatch.GetTimestamp();
            if (canPlanThisTick)
            {
                // #214 phase movement owns room exits and safe-room selection. The legacy closure
                // evacuation remains the fallback for matches that do not use the phase machine.
                isEvacuating = TryMaintainSurvivorPhaseMovement(
                    bot,
                    matchingId,
                    survivorPhase,
                    closureManager,
                    inventoryManager,
                    playerAreas);
                if (!isEvacuating)
                {
                    isEvacuating = TryMaintainClosureEvacuation(
                        bot, matchingId, closureManager, hasOpenNonCorridorRefuge);
                }
                // 목적지 커밋이 살아 있으면 잔상 사냥 계획을 아예 타지 않는다. 그래서 방에서
                // 굳은 봇은 계획 안쪽의 정체 판정에 닿지도 못한다. 커밋을 먼저 풀어 다음 단계가
                // 새 목적지를 고를 기회를 만든다. 대피는 정체보다 우선이므로 건드리지 않는다.
                if (!isEvacuating && IsRoomHuntStalled(bot))
                {
                    // 커밋 해제만으로도 봇은 재계획으로 밀려난다. 사냥 계획까지 도달하는 경우만
                    // 세면 실제 발동을 크게 과소 집계하므로 여기서 기록한다.
                    _logger.LogInformation(
                        "Bot room hunt stalled: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, " +
                        "DroppedDestination={DroppedDestination}",
                        matchingId, bot.PlayerId, bot.CurrentArea, bot.MovementDestination);
                    bot.RoomHuntEscapeRequested = true;
                    bot.MovementDestination = AreaType.None;
                    bot.LoopWaitUntil = DateTime.MinValue;
                    // 재계획은 경로가 비었을 때만 돈다. 방 안 kite 경로가 계속 갱신되면 경로가
                    // 마르지 않아 사냥 계획에 영영 닿지 못한다. 진행이 없는 상태이므로 버려도 잃을 게 없다.
                    bot.Path.Clear();
                    bot.PathIndex = 0;
                    bot.RoomHuntStartedAtUtc = DateTime.UtcNow;
                }

                isCommittingToDestination = !isEvacuating &&
                                            TryMaintainMovementDestination(bot, matchingId, closureManager);
                if (!isEvacuating &&
                    TryAutoPickupGroundItem(bot, matchingId, inventoryManager, groundItemManager, out var pickup) &&
                    pickup.HasValue)
                {
                    result.GroundItemPickups.Add(pickup.Value);
                }
                if (!isEvacuating && !isCommittingToDestination)
                {
                    UpdateCombatMovementIntent(bot, matchingId, closureManager, combatTargets);
                    TryStartPveKitePath(bot, matchingId, closureManager, pveTargets ?? []);
                }
                if (!hasOpenNonCorridorRefuge && bot.PathIndex >= bot.Path.Count &&
                    !bot.IsInInteraction && bot.PendingRngInteractId == 0 && bot.PendingChecklistTaskId == 0)
                {
                    TryStartLastStandPatrolPath(bot, matchingId, closureManager);
                }
            }
            result.PlanningElapsedMilliseconds += Stopwatch.GetElapsedTime(planningStartedAt).TotalMilliseconds;

            long walkingStartedAt = Stopwatch.GetTimestamp();
            var ev = WalkStep(
                bot,
                matchingId,
                closureManager,
                areaItemStockManager,
                inventoryManager,
                playerAreas,
                checklistManager,
                pveTargets ?? [],
                canPlanThisTick);
            result.WalkingElapsedMilliseconds += Stopwatch.GetElapsedTime(walkingStartedAt).TotalMilliseconds;
            if (ev != null) result.Movements.Add(ev);
            if (bot.PendingExploreEndBroadcast)
            {
                result.ExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
                bot.PendingExploreEndBroadcast = false;
            }
        }
        return result;
    }

    private long SelectMovementPlanningBot(long matchingId, IReadOnlyList<BotPlayerState> activeBots)
    {
        int cursor = _botMovementPlanningCursors.AddOrUpdate(
            matchingId,
            0,
            (_, current) => (current + 1) % activeBots.Count);
        return activeBots[cursor % activeBots.Count].PlayerId;
    }

    private bool TryMaintainSurvivorPhaseMovement(
        BotPlayerState bot,
        long matchingId,
        SurvivorPhaseSnapshot phase,
        AreaClosureManager closureManager,
        InGameInventoryManager inventoryManager,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        if (phase.MatchingId != matchingId)
            return false;

        switch (phase.Phase)
        {
            case SurvivorMatchPhase.ROOM_CLOSURE_WARNING:
                bot.SurvivorRoomChoice = AreaType.None;
                if (bot.CurrentArea.IsCorridor())
                    return true;
                return TryStageBotAtCorridorExit(bot, matchingId, closureManager);

            case SurvivorMatchPhase.CORRIDOR_ENTRY:
            case SurvivorMatchPhase.CORRIDOR_COMBAT:
                bot.SurvivorRoomChoice = AreaType.None;
                if (bot.CurrentArea.IsCorridor())
                    return false;
                return TryCommitSurvivorPhaseDestination(
                    bot, matchingId, AreaType.Corridor, closureManager);

            case SurvivorMatchPhase.ROOM_SELECTION:
            case SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING:
                if (phase.NextRooms.Contains(bot.CurrentArea))
                {
                    bot.Path.Clear();
                    bot.PathIndex = 0;
                    bot.MovementDestination = AreaType.None;
                    return true;
                }

                if (!phase.NextRooms.Contains(bot.SurvivorRoomChoice))
                {
                    bot.SurvivorRoomChoice = ChooseSurvivorSafeRoom(
                        bot, matchingId, phase.NextRooms, closureManager, inventoryManager, playerAreas);
                }

                if (bot.SurvivorRoomChoice == AreaType.None)
                    return true;

                return TryCommitSurvivorPhaseDestination(
                    bot, matchingId, bot.SurvivorRoomChoice, closureManager);

            case SurvivorMatchPhase.FINAL:
                bot.SurvivorRoomChoice = AreaType.Ground;
                if (bot.CurrentArea == AreaType.Ground)
                    return false;
                return TryCommitSurvivorPhaseDestination(
                    bot, matchingId, AreaType.Ground, closureManager);

            case SurvivorMatchPhase.ROOM_COMBAT:
                bot.SurvivorRoomChoice = AreaType.None;
                return !phase.CurrentRooms.Contains(bot.CurrentArea);

            default:
                return false;
        }
    }

    private bool TryStageBotAtCorridorExit(
        BotPlayerState bot,
        long matchingId,
        AreaClosureManager closureManager)
    {
        if (bot.MovementDestination == AreaType.Corridor && bot.PathIndex < bot.Path.Count)
            return true;

        CancelBotActionForEvacuation(bot);
        MapId mapId = GetMatchingMapId(matchingId);
        var fullPath = BotPathfinder.FindPath(
            mapId,
            bot.CurrentArea,
            bot.Cell,
            AreaType.Corridor,
            GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, AreaType.Corridor)
            ?? GameMapData.GetAreaSpawnCell(mapId, AreaType.Corridor),
            area => IsAreaClosingOrClosed(closureManager, matchingId, area));

        if (fullPath == null)
            return true;

        // The corridor remains closed during the warning. Walk to the final in-room
        // doorway cell now, then cross only after CORRIDOR_ENTRY opens it.
        var stagingPath = fullPath
            .TakeWhile(step => !step.IsAreaTransition && step.Area == bot.CurrentArea)
            .ToList();
        bot.Path = stagingPath;
        bot.PathIndex = 0;
        bot.MovementDestination = AreaType.Corridor;
        bot.LoopWaitUntil = DateTime.MinValue;
        return true;
    }

    private bool TryCommitSurvivorPhaseDestination(
        BotPlayerState bot,
        long matchingId,
        AreaType destination,
        AreaClosureManager closureManager)
    {
        if (destination == AreaType.None || bot.CurrentArea == destination)
            return false;
        if (bot.MovementDestination == destination && bot.PathIndex < bot.Path.Count)
            return true;

        CancelBotActionForEvacuation(bot);
        MapId mapId = GetMatchingMapId(matchingId);
        var path = BotPathfinder.FindPath(
            mapId,
            bot.CurrentArea,
            bot.Cell,
            destination,
            GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, destination)
            ?? GameMapData.GetAreaSpawnCell(mapId, destination),
            area => IsAreaClosingOrClosed(closureManager, matchingId, area));

        if (path is not { Count: > 0 })
        {
            bot.LoopWaitUntil = DateTime.UtcNow.AddMilliseconds(250);
            return true;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.MovementDestination = destination;
        bot.LoopWaitUntil = DateTime.MinValue;
        _logger.LogInformation(
            "Bot survivor phase move: MatchingId={MatchingId}, BotId={BotId}, {From}->{To}, Steps={Steps}",
            matchingId, bot.PlayerId, bot.CurrentArea, destination, path.Count);
        return true;
    }

    private AreaType ChooseSurvivorSafeRoom(
        BotPlayerState bot,
        long matchingId,
        IReadOnlyList<AreaType> nextRooms,
        AreaClosureManager closureManager,
        InGameInventoryManager inventoryManager,
        IReadOnlyDictionary<long, AreaType> playerAreas)
    {
        if (nextRooms.Count == 0)
            return AreaType.None;

        MapId mapId = GetMatchingMapId(matchingId);
        float corruptionRatio = Math.Clamp(
            bot.Corruption / (float)Config.SURVIVOR_MAX_CORRUPTION, 0f, 1f);
        var boardItemIds = inventoryManager.GetPlayerInventory(matchingId, bot.PlayerId)
            .GetAllItems()
            .Where(item => item.Count > 0)
            .Select(item => item.ItemId)
            .ToArray();
        SurvivorOrbData.TryGetDominantPveColor(boardItemIds, out SurvivorOrbColor dominantColor);

        var candidates = nextRooms
            .Distinct()
            .Select(area =>
            {
                var path = BotPathfinder.FindPath(
                    mapId,
                    bot.CurrentArea,
                    bot.Cell,
                    area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, area)
                    ?? GameMapData.GetAreaSpawnCell(mapId, area),
                    blocked => IsAreaClosingOrClosed(closureManager, matchingId, blocked));
                int occupants = playerAreas.Count(entry =>
                    entry.Key != bot.PlayerId && entry.Value == area);
                float pressureWeight = corruptionRatio >= 0.65f ? 10f : 2.5f;
                float score = (path?.Count ?? 10000) * 0.08f + occupants * pressureWeight;

                int coreRewardItemId = EmotionAfterimageMonsterSpawnData.Definitions
                    .Where(definition => definition.Area == area && definition.IsCore)
                    .Select(definition => definition.RewardItemId)
                    .FirstOrDefault();
                if (dominantColor != SurvivorOrbColor.None &&
                    SurvivorOrbData.TryGetColorAndTier(coreRewardItemId, out SurvivorOrbColor roomColor, out _))
                {
                    float affinity = SurvivorOrbData.GetPveDamageMultiplier(dominantColor, roomColor);
                    score += affinity > 1f ? -3f : affinity < 1f ? 4f : 0f;
                }

                // Healthy bots may deliberately contest an occupied room; damaged bots seek space.
                if (corruptionRatio < 0.35f && occupants > 0)
                    score -= 2f;

                return new { Area = area, Path = path, Score = score };
            })
            .Where(candidate => candidate.Path is { Count: > 0 })
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs((int)(bot.PlayerId % 97) - (int)candidate.Area))
            .FirstOrDefault();

        return candidates?.Area ?? AreaType.None;
    }

    private bool TryMaintainClosureEvacuation(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager, bool hasOpenNonCorridorRefuge)
    {
        var closure = closureManager.GetClientStateSnapshot(matchingId);
        var unavailableAreas = closure.ClosedAreas.Concat(closure.WarningAreas).ToHashSet();
        bool currentAreaUnsafe = unavailableAreas.Contains(bot.CurrentArea);

        if (bot.EvacuationDestination != AreaType.None &&
            (unavailableAreas.Contains(bot.EvacuationDestination) || bot.EvacuationDestination.IsCorridor()))
        {
            bot.EvacuationDestination = AreaType.None;
        }

        // A warning lasts for multiple movement ticks. Keep the selected escape route while the
        // bot is still walking it; otherwise every 50ms picks a different safe room at the door.
        if (bot.EvacuationDestination != AreaType.None && bot.PathIndex < bot.Path.Count)
            return true;

        if (!currentAreaUnsafe && bot.EvacuationDestination != AreaType.None)
        {
            if (bot.CurrentArea == bot.EvacuationDestination)
            {
                bot.EvacuationDestination = AreaType.None;
                return false;
            }

            // Combat retreats reuse EvacuationDestination. A corridor transition can consume
            // the old path before the room change is observed; rebuild it instead of idling.
            var routeMapId = GetMatchingMapId(matchingId);
            var targetCell = GameAreaConnectionData.GetSpawnCell(
                    routeMapId, bot.CurrentArea, bot.EvacuationDestination)
                ?? GameMapData.GetAreaSpawnCell(routeMapId, bot.EvacuationDestination);
            var restoredPath = BotPathfinder.FindPath(
                routeMapId,
                bot.CurrentArea,
                bot.Cell,
                bot.EvacuationDestination,
                targetCell,
                unavailableAreas.Contains);
            if (restoredPath is { Count: > 0 })
            {
                bot.Path = restoredPath;
                bot.PathIndex = 0;
                bot.LoopWaitUntil = DateTime.MinValue;
                _logger.LogDebug(
                    "Bot restored evacuation route: BotId={Bot}, {From}->{To}, Steps={Steps}",
                    bot.PlayerId, bot.CurrentArea, bot.EvacuationDestination, restoredPath.Count);
                return true;
            }

            // The destination no longer has a valid route. Let normal room selection recover.
            bot.EvacuationDestination = AreaType.None;
            bot.LoopWaitUntil = DateTime.MinValue;
            return false;
        }

        if (!currentAreaUnsafe)
            return false;

        CancelBotActionForEvacuation(bot);
        var mapId = GetMatchingMapId(matchingId);
        var candidate = GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() &&
                           area != bot.CurrentArea && !unavailableAreas.Contains(area))
            .Select(area => new
            {
                Area = area,
                Path = BotPathfinder.FindPath(
                    mapId, bot.CurrentArea, bot.Cell, area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, area)
                    ?? GameMapData.GetAreaSpawnCell(mapId, area),
                    unavailableAreas.Contains)
            })
            .Where(entry => entry.Path is { Count: > 0 })
            .OrderBy(entry => CountEvacuationReservations(matchingId, entry.Area))
            .ThenBy(entry => entry.Area == AreaType.Ground ? 1 : 0)
            .ThenBy(entry => entry.Path!.Count)
            .ThenBy(entry => Math.Abs((int)(bot.PlayerId % 97) - (int)entry.Area))
            .FirstOrDefault();

        if (candidate?.Path == null)
        {
            if (!hasOpenNonCorridorRefuge)
            {
                // No room remains to escape to. Do not keep the bot in an evacuation
                // wait loop: overtime still applies, but it must remain an active target.
                bot.EvacuationDestination = AreaType.None;
                bot.Path.Clear();
                bot.PathIndex = 0;
                bot.LoopWaitUntil = DateTime.MinValue;
                _logger.LogInformation(
                    "Bot last stand started: MatchingId={MatchingId}, BotId={BotId}, Area={Area}",
                    matchingId, bot.PlayerId, bot.CurrentArea);
                return false;
            }

            bot.LoopWaitUntil = DateTime.UtcNow.AddMilliseconds(250);
            return true;
        }

        bot.EvacuationDestination = candidate.Area;
        bot.Path = candidate.Path;
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.NextCombatRepathAt = DateTime.UtcNow.AddSeconds(1);
        _logger.LogInformation(
            "Bot closure evacuation: MatchingId={MatchingId}, BotId={BotId}, {From}->{To}, Steps={Steps}",
            matchingId, bot.PlayerId, bot.CurrentArea, candidate.Area, candidate.Path.Count);
        return true;
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
    /// Keeps a bot moving while it farms a nearby afterimage pack. This is intentionally
    /// an in-room lateral weave: closure evacuation and committed room travel keep priority.
    /// </summary>
    private bool TryStartPveKitePath(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager, IReadOnlyCollection<MonsterCombatTarget> pveTargets)
    {
        var now = DateTime.UtcNow;
        if (pveTargets.Count == 0 || bot.CurrentArea == AreaType.None || bot.CurrentArea.IsCorridor() ||
            bot.EvacuationDestination != AreaType.None || bot.MovementDestination != AreaType.None ||
            bot.PathIndex < bot.Path.Count || now < bot.NextPveKiteRepathAt)
        {
            return false;
        }

        var nearby = pveTargets
            .Where(target => target.Area == bot.CurrentArea &&
                             DistanceSquared(bot.Position, target.Position.X, target.Position.Y) <=
                             PveKiteThreatRange * PveKiteThreatRange)
            .OrderBy(target => DistanceSquared(bot.Position, target.Position.X, target.Position.Y))
            .ToList();
        if (nearby.Count == 0)
            return false;

        float nearestDistance = MathF.Sqrt(DistanceSquared(bot.Position, nearby[0].Position.X, nearby[0].Position.Y));
        if (nearby.Count < 2 && nearestDistance > PveKiteImmediateThreatRange)
            return false;

        float centerX = nearby.Average(target => target.Position.X);
        float centerY = nearby.Average(target => target.Position.Y);
        float awayX = bot.Position.X - centerX;
        float awayY = bot.Position.Y - centerY;
        float awayLength = MathF.Sqrt(awayX * awayX + awayY * awayY);
        if (awayLength < 0.01f)
        {
            float fallbackAngle = MathF.Abs(bot.PlayerId % 11) * (MathF.Tau / 11f);
            awayX = MathF.Cos(fallbackAngle);
            awayY = MathF.Sin(fallbackAngle);
            awayLength = 1f;
        }
        awayX /= awayLength;
        awayY /= awayLength;

        int weaveSide = ((Math.Abs(bot.PlayerId) + (long)(now - DateTime.UnixEpoch).TotalSeconds) & 1) == 0 ? 1 : -1;
        float tangentX = -awayY * weaveSide;
        float tangentY = awayX * weaveSide;
        float directionX = awayX * 0.45f + tangentX * 0.9f;
        float directionY = awayY * 0.45f + tangentY * 0.9f;
        float directionLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
        directionX /= directionLength;
        directionY /= directionLength;

        var mapId = GetMatchingMapId(matchingId);
        foreach (float angleOffset in new[] { 0f, 0.55f, -0.55f, 1.1f, -1.1f })
        {
            float cosine = MathF.Cos(angleOffset);
            float sine = MathF.Sin(angleOffset);
            float candidateX = directionX * cosine - directionY * sine;
            float candidateY = directionX * sine + directionY * cosine;
            var targetCell = WorldToCell(new Vector3f(
                bot.Position.X + candidateX * PveKiteStepDistance,
                bot.Position.Y + candidateY * PveKiteStepDistance,
                0f));
            if (GameMapData.GetCurrentArea(mapId, targetCell) != bot.CurrentArea ||
                !GameMapData.IsMoveablePosition(mapId, targetCell))
            {
                continue;
            }

            var path = BotPathfinder.FindPath(
                mapId,
                bot.CurrentArea,
                bot.Cell,
                bot.CurrentArea,
                targetCell,
                area => area != bot.CurrentArea || IsAreaClosingOrClosed(closureManager, matchingId, area));
            if (path is not { Count: > 0 })
                continue;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.NextPveKiteRepathAt = now.AddMilliseconds(
                GetPveKiteRepathDelayMilliseconds(bot.PlayerId, 850));
            _logger.LogDebug(
                "Bot PVE kite: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, Threats={Threats}, Steps={Steps}",
                matchingId, bot.PlayerId, bot.CurrentArea, nearby.Count, path.Count);
            return true;
        }

        bot.NextPveKiteRepathAt = now.AddMilliseconds(
            GetPveKiteRepathDelayMilliseconds(bot.PlayerId, 400));
        return false;
    }

    private static double GetPveKiteRepathDelayMilliseconds(long playerId, int baseMilliseconds) =>
        baseMilliseconds + Math.Abs(playerId % 7) * 90d;

    private bool TryStartLastStandPatrolPath(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager)
    {
        if (bot.CurrentArea == AreaType.None || bot.CurrentArea.IsCorridor())
            return false;

        var mapId = GetMatchingMapId(matchingId);
        int startIndex = Math.Abs((int)(bot.PlayerId % LastStandPatrolOffsets.Length));
        foreach (var offsetIndex in Enumerable.Range(0, LastStandPatrolOffsets.Length))
        {
            var offset = LastStandPatrolOffsets[(startIndex + offsetIndex) % LastStandPatrolOffsets.Length];
            var candidate = new Cell(bot.Cell.X + offset.X, bot.Cell.Y + offset.Y);
            if (GameMapData.GetCurrentArea(mapId, candidate) != bot.CurrentArea ||
                !GameMapData.IsMoveablePosition(mapId, candidate))
            {
                continue;
            }

            var path = BotPathfinder.FindPath(
                mapId,
                bot.CurrentArea,
                bot.Cell,
                bot.CurrentArea,
                candidate,
                area => area != bot.CurrentArea && IsAreaClosingOrClosed(closureManager, matchingId, area));
            if (path is not { Count: > 0 })
                continue;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.MovementDestination = AreaType.None;
            bot.LoopWaitUntil = DateTime.MinValue;
            return true;
        }

        return false;
    }
    private bool TryMaintainMovementDestination(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager)
    {
        var destination = bot.MovementDestination;
        if (destination == AreaType.None)
            return false;

        if (destination.IsCorridor() || IsAreaClosingOrClosed(closureManager, matchingId, destination))
        {
            bot.MovementDestination = AreaType.None;
            return false;
        }

        if (bot.CurrentArea == destination)
        {
            bot.MovementDestination = AreaType.None;
            return false;
        }

        if (bot.PathIndex < bot.Path.Count)
            return true;

        var mapId = GetMatchingMapId(matchingId);
        var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, destination)
            ?? GameMapData.GetAreaSpawnCell(mapId, destination);
        var path = BotPathfinder.FindPath(
            mapId,
            bot.CurrentArea,
            bot.Cell,
            destination,
            targetCell,
            area => IsAreaClosingOrClosed(closureManager, matchingId, area));
        if (path == null || path.Count == 0)
        {
            bot.MovementDestination = AreaType.None;
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.6);
            return false;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        _logger.LogDebug(
            "Bot restored committed route: BotId={Bot}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.CurrentArea, destination, path.Count);
        return true;
    }

    private int CountEvacuationReservations(long matchingId, AreaType area)
    {
        return _botStates.TryGetValue(matchingId, out var bots)
            ? bots.Count(other => !other.IsEliminated && other.EvacuationDestination == area)
            : 0;
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

    private static bool IsRecentCombatRetreatOrigin(BotPlayerState bot, AreaType area) =>
        area != AreaType.None &&
        area == bot.RecentCombatRetreatOrigin &&
        DateTime.UtcNow < bot.CombatRetreatOriginBlockedUntil;

    /// <summary>
    ///     방 사냥이 막혔는지 판정한다. 팩 하나를 정리하는 정상 체류는 20초 안쪽이므로
    ///     (2026-07-31 match-2008 계측: 활발한 봇 최장 20초, 사람 24초),
    ///     그 두 배가 지나도록 같은 방에 있으면 진전이 없는 것으로 본다.
    /// </summary>
    private static bool IsRoomHuntStalled(BotPlayerState bot) =>
        !bot.CurrentArea.IsCorridor() &&
        (DateTime.UtcNow - bot.RoomHuntStartedAtUtc).TotalSeconds >= RoomHuntStallSeconds;

    private static void CancelBotActionForEvacuation(BotPlayerState bot)
    {
        bool hadAction = bot.IsInInteraction || bot.PendingRngInteractId != 0 ||
                         bot.PendingChecklistTaskId != 0 || bot.PendingForcedInteractId != 0;
        bot.IsInInteraction = false;
        bot.InteractionStayUntil = DateTime.MinValue;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.PendingForcedInteractArea = AreaType.None;
        bot.PendingForcedInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        bot.RestUntil = DateTime.MinValue;
        bot.TransitionPauseUntil = DateTime.MinValue;
        bot.MovementDestination = AreaType.None;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
        ClearBotRoomExplorePlan(bot);
        if (hadAction)
            bot.PendingExploreEndBroadcast = true;
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
        ChecklistManager checklistManager, IReadOnlyCollection<MonsterCombatTarget> pveTargets,
        bool allowPathPlanning)
    {
        var now = DateTime.UtcNow;
        float deltaSec = (float)(now - bot.LastWalkStepTime).TotalSeconds;
        if (deltaSec <= 0) deltaSec = 0.25f;
        bot.LastWalkStepTime = now;

        // 상호작용 중에는 walking 정지 (실제 플레이어 정지 동작과 동등).
        // InteractionStayUntil 시각이 지나면 자동 해제.
        if (bot.IsInInteraction)
        {
            if (now >= bot.InteractionStayUntil) bot.IsInInteraction = false;
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
            if (bot.PendingRngInteractId != 0 || bot.PendingChecklistTaskId != 0) return null;

            if (!allowPathPlanning) return null;
            ChooseNewWanderTarget(bot, matchingId, closureManager, areaItemStockManager, inventoryManager, playerAreas,
                checklistManager, pveTargets);
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

        // Walk every waypoint at the same speed. An area transition is just the adjacent cell across a door.
        var targetPos = CellToWorldPosition(mapId, nextStep.Cell);
        float dx = targetPos.X - bot.Position.X;
        float dy = targetPos.Y - bot.Position.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float maxDist = BotWalkSpeed * GetBotMovementSpeedMultiplier(bot) * deltaSec;
        if (dist >= 0.01f)
            maxDist = ScaledWalkSpeed(dx / dist, dy / dist, bot.WindResonanceActive) * GetBotWaveSlowMultiplier(bot) * deltaSec;

        Vector3f newPosition;
        Vector3f velocity;

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
                    velocity = ScaledWalkVelocity(nextDx / nextDist, nextDy / nextDist, bot.WindResonanceActive) * GetBotWaveSlowMultiplier(bot);

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
            velocity = ScaledWalkVelocity(dirX, dirY, bot.WindResonanceActive) * GetBotWaveSlowMultiplier(bot);
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
                bot.RoomHuntStartedAtUtc = DateTime.UtcNow;
                bot.RoomHuntEscapeRequested = false;
                ClearBotRoomExplorePlan(bot);
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

    /// <summary>봇 도착 후 다음 영역으로 출발 전 대기 시간 (자연스러운 휴식).</summary>
    private const double BotArrivalWaitMinSeconds = 2.0;
    private const double BotArrivalWaitMaxSeconds = 5.5;

    /// <summary>
    ///     프로토 0: 봇이 도착했거나 경로가 비었을 때 다음 목적지(3·4층 방) 선택 + 경로 계산.
    ///     - 따라가기: 타겟(TargetPlayerId)이 있는 방으로 이동(회복).
    ///     - 떠보기: 일정 확률로 최저 인원 방으로 이동(추적자 유인).
    ///     복도는 목적지가 아니라 통과만(transit). 미션 수집 동선(직책 큐/RNG 채집)은 폐기.
    /// </summary>
    private void ChooseNewWanderTarget(BotPlayerState bot, long matchingId, AreaClosureManager closureManager,
        AreaItemStockManager areaItemStockManager, InGameInventoryManager inventoryManager,
        IReadOnlyDictionary<long, AreaType> playerAreas,
        ChecklistManager checklistManager, IReadOnlyCollection<MonsterCombatTarget> pveTargets)
    {
        var mapId = GetMatchingMapId(matchingId);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.TransitionPauseUntil = DateTime.MinValue;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.PendingExploreEndBroadcast = false;

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

        if (Config.CHECKLIST_SYSTEM_ENABLED)
        {
            var activeSchoolTask = checklistManager.GetNextActiveGeneralInteractTask(matchingId, bot.PlayerId);
            int schoolTaskCost = activeSchoolTask != null ? Math.Max(0, activeSchoolTask.StaminaCost) : 0;
            if (activeSchoolTask != null &&
                bot.Stamina >= schoolTaskCost &&
                TryStartBotChecklistTaskPath(bot, matchingId, activeSchoolTask, closureManager))
            {
                return;
            }
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
        if (IsRecentCombatRetreatOrigin(bot, destination))
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.6);
            return;
        }
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
        // 같은 방에 오래 머물렀는데 아직 잔상이 남아 있다면 사냥이 막힌 상태다. 오브가 닿지
        // 않는 위치, 결판나지 않는 대치, 이미 남이 차지한 팩이 원인이며, 어느 쪽이든 그 방을
        // 계속 최우선 후보로 두면 봇이 제자리에 굳는다. 그때는 현재 지역을 후보에서 뺀다.
        bool roomHuntStalled = bot.RoomHuntEscapeRequested;
        // 복도 잔상도 목적지가 된다. 폐쇄 페이즈마다 보상이 2배로 커지는데 봇만 방에
        // 묶여 있으면, 사람만 아는 복도 파밍이 그대로 격차가 된다 (2026-07-31 match-2015:
        // 사람 소환석 425 중 412가 복도, 봇 최고치는 123 중 20).
        // 다만 이미 복도에 서 있는 봇에게 복도를 다시 목적지로 주면 제자리에서 맴돈다.
        // 그 경우는 자동전투가 근처 잔상을 알아서 잡으므로 목적지에서만 뺀다.
        var areaGroups = pveTargets
            .Where(target => target.MapId == mapId &&
                             target.Area != AreaType.None &&
                             !(target.Area.IsCorridor() && bot.CurrentArea.IsCorridor()) &&
                             !unavailable.Contains(target.Area) &&
                             !IsRecentCombatRetreatOrigin(bot, target.Area) &&
                             !(roomHuntStalled && target.Area == bot.CurrentArea))
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
        if (roomHuntStalled)
        {
            // 탈출 요청이 실제로 어디로 이어졌는지만 남긴다. 발동 집계는 이동 루프 상단에서 한다.
            _logger.LogInformation(
                "Bot room hunt escape: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, " +
                "Escape={Escape}, Candidates={Candidates}, PveAreas={PveAreas}",
                matchingId, bot.PlayerId, bot.CurrentArea,
                selected?.Area.ToString() ?? "none", candidates.Count, areaGroups.Count);
            bot.RoomHuntEscapeRequested = false;
        }

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
            if (SurvivorOrbData.TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier))
            {
                score += tier * SurvivorOrbData.GetPveDamageMultiplier(color, monsterRewardItemId);
                continue;
            }

            if (SurvivorOrbData.TryGetRecoveryTier(itemId, out int recoveryTier))
                score += recoveryTier * 0.25f;
        }

        return score;
    }

    private bool TryStartPostExploreRelocation(BotPlayerState bot, long matchingId, MapId mapId,
        AreaClosureManager closureManager, AreaItemStockManager areaItemStockManager,
        bool requireSecludedArea)
    {
        var candidateAreas = GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None &&
                           area != bot.CurrentArea &&
                           !area.IsCorridor() &&
                           !IsRecentCombatRetreatOrigin(bot, area) &&
                           (!requireSecludedArea || IsSecludedFarmingArea(mapId, area)) &&
                           !bot.CompletedRoomExploreAreas.Contains(area) &&
                           !IsAreaClosingOrClosed(closureManager, matchingId, area) &&
                           areaItemStockManager.HasRemaining(matchingId, (int)area) &&
                           GameInteractableData.GetByZone((int)area)
                               .Any(info => IsBotRoomExploreAvailable(bot, matchingId, info, area)))
            .Select(area => new
            {
                Area = area,
                CanSpawnBattleItem = GameInteractableData.GetItemPoolByArea((int)area)
                    .Any(BattleItemCombatData.IsCombatItem)
            })
            .OrderByDescending(candidate => bot.EquippedBattleItemId <= 0 && candidate.CanSpawnBattleItem)
            .ThenBy(candidate => CountAreaPressure(matchingId, candidate.Area))
            .ThenBy(_ => _rng.Next())
            .Select(candidate => candidate.Area)
            .ToList();

        foreach (var destination in candidateAreas)
        {
            var targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, destination)
                ?? GameMapData.GetAreaSpawnCell(mapId, destination);
            var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
                destination, targetCell,
                area => IsAreaClosingOrClosed(closureManager, matchingId, area));
            if (path == null || path.Count == 0) continue;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.MovementDestination = destination;
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.4, 1.0);
            _logger.LogInformation(
                "Bot post-explore relocation: BotId={Bot}, {From}->{To}, Steps={Steps}",
                bot.PlayerId, bot.CurrentArea, destination, path.Count);
            return true;
        }

        return false;
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
        bot.PendingExploreEndBroadcast = true;
        _logger.LogInformation(
            "Bot forced follow move: MatchingId={MatchingId}, BotId={Bot}, Target={Target}, {From}->{To}, Steps={Steps}",
            matchingId, bot.PlayerId, bot.TargetPlayerId, bot.CurrentArea, targetArea, path.Count);
        return true;
    }

    private bool TryStartBotChecklistTaskPath(BotPlayerState bot, long matchingId, ChecklistTaskData task,
        AreaClosureManager closureManager)
    {
        var area = (AreaType)task.AreaType;
        if (area == AreaType.None || IsAreaClosingOrClosed(closureManager, matchingId, area)) return false;

        var info = GameInteractableData.Get(task.InteractId);
        if (info == null || info.ZoneId != task.AreaType) return false;
        if (info.CellX == 0 && info.CellY == 0) return false;

        return TryStartBotChecklistPath(bot, matchingId, area, task.InteractId, task.TaskId, closureManager);
    }

    private bool TryStartQueuedRoomExplore(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager, AreaItemStockManager areaItemStockManager)
    {
        var mapId = GetMatchingMapId(matchingId);
        if (bot.CurrentArea == AreaType.None || bot.CurrentArea.IsCorridor() ||
            bot.EquippedBattleItemId <= 0 && !IsSecludedFarmingArea(mapId, bot.CurrentArea) ||
            IsAreaClosingOrClosed(closureManager, matchingId, bot.CurrentArea))
        {
            ClearBotRoomExplorePlan(bot);
            return false;
        }

        if (bot.CompletedRoomExploreAreas.Contains(bot.CurrentArea))
            return false;
        if (!areaItemStockManager.HasRemaining(matchingId, (int)bot.CurrentArea))
        {
            MarkBotRoomExploreComplete(bot);
            return false;
        }

        if (bot.RoomExploreQueueArea != AreaType.None && bot.RoomExploreQueueArea != bot.CurrentArea)
        {
            bot.InteractQueueInArea.Clear();
            bot.RoomExploreQueueArea = AreaType.None;
        }

        if (bot.InteractQueueInArea.Count == 0)
        {
            // 같은 방 방문에서 이미 만든 큐를 전부 소비했다면 정적 후보를 다시 채우지 않는다.
            if (bot.RoomExploreQueueArea == bot.CurrentArea ||
                !RefillRoomExploreQueue(bot, matchingId, areaItemStockManager))
            {
                MarkBotRoomExploreComplete(bot);
                return false;
            }
        }

        while (bot.InteractQueueInArea.Count > 0)
        {
            if (!areaItemStockManager.HasRemaining(matchingId, (int)bot.CurrentArea))
            {
                MarkBotRoomExploreComplete(bot);
                return false;
            }

            int interactId = bot.InteractQueueInArea[0];
            bot.InteractQueueInArea.RemoveAt(0);

            var info = GameInteractableData.Get(interactId);
            if (!IsBotRoomExploreAvailable(bot, matchingId, info, bot.CurrentArea))
                continue;

            if (!TryStartBotInteractPath(bot, matchingId, bot.CurrentArea, interactId, closureManager,
                    clearInteractQueue: false))
                continue;

            _logger.LogInformation(
                "Bot room explore queued: BotId={Bot}, Area={Area}, InteractId={InteractId}, Remaining={Remaining}",
                bot.PlayerId, bot.CurrentArea, interactId, bot.InteractQueueInArea.Count);
            return true;
        }

        MarkBotRoomExploreComplete(bot);
        return false;
    }

    private bool RefillRoomExploreQueue(BotPlayerState bot, long matchingId,
        AreaItemStockManager areaItemStockManager)
    {
        if (!areaItemStockManager.HasRemaining(matchingId, (int)bot.CurrentArea))
            return false;

        var candidates = GameInteractableData.GetByZone((int)bot.CurrentArea)
            .Where(info => IsBotRoomExploreAvailable(bot, matchingId, info, bot.CurrentArea))
            .OrderBy(_ => _rng.Next())
            .Select(info => info.Id)
            .ToList();

        if (candidates.Count == 0)
            return false;

        bot.InteractQueueInArea.Clear();
        bot.InteractQueueInArea.AddRange(candidates);
        bot.RoomExploreQueueArea = bot.CurrentArea;
        return true;
    }

    private static bool IsBotRoomExploreAvailable(BotPlayerState bot, long matchingId,
        InteractableInfoData? info, AreaType area)
    {
        return IsBotRoomExploreCandidate(info, area) &&
               !bot.ExploredRngInteractIds.Contains(info!.Id) &&
               !RngCollectCooldownStore.IsInCooldown(matchingId, info.Id, out _);
    }

    private static bool IsBotRoomExploreCandidate(InteractableInfoData? info, AreaType area)
    {
        return info != null &&
               info.ZoneId == (int)area &&
               info.InteractionType == InteractionType.RNG_COLLECT &&
               (info.CellX != 0 || info.CellY != 0);
    }

    private static void ClearBotRoomExplorePlan(BotPlayerState bot)
    {
        bot.InteractQueueInArea.Clear();
        bot.RoomExploreQueueArea = AreaType.None;
    }

    private static void MarkBotRoomExploreComplete(BotPlayerState bot)
    {
        bot.InteractQueueInArea.Clear();
        bot.RoomExploreQueueArea = AreaType.None;
        if (bot.CurrentArea != AreaType.None && !bot.CurrentArea.IsCorridor())
            bot.CompletedRoomExploreAreas.Add(bot.CurrentArea);
        bot.LoopWaitUntil = DateTime.MinValue;
    }

    private bool TryStartRecoveryRngPath(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager, AreaItemStockManager areaItemStockManager)
    {
        var mapId = GetMatchingMapId(matchingId);
        bool requireSecludedArea = bot.EquippedBattleItemId <= 0;
        var areaOrder = new List<AreaType>();
        if (bot.CurrentArea != AreaType.None &&
            !bot.CurrentArea.IsCorridor() &&
            (!requireSecludedArea || IsSecludedFarmingArea(mapId, bot.CurrentArea)) &&
            !bot.CompletedRoomExploreAreas.Contains(bot.CurrentArea) &&
            !IsAreaClosingOrClosed(closureManager, matchingId, bot.CurrentArea) &&
            areaItemStockManager.HasRemaining(matchingId, (int)bot.CurrentArea))
        {
            areaOrder.Add(bot.CurrentArea);
        }

        areaOrder.AddRange(GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None &&
                           area != bot.CurrentArea &&
                           !area.IsCorridor() &&
                           !IsRecentCombatRetreatOrigin(bot, area) &&
                           !bot.CompletedRoomExploreAreas.Contains(area) &&
                           (!requireSecludedArea || IsSecludedFarmingArea(mapId, area)) &&
                           !IsAreaClosingOrClosed(closureManager, matchingId, area) &&
                           areaItemStockManager.HasRemaining(matchingId, (int)area))
            .OrderBy(area => CountAreaPressure(matchingId, area))
            .ThenBy(_ => _rng.Next()));

        foreach (var area in areaOrder)
        {
            var candidates = GameInteractableData.GetByZone((int)area)
                .Where(info => IsBotRoomExploreAvailable(bot, matchingId, info, area))
                .OrderBy(_ => _rng.Next())
                .ToList();

            foreach (var info in candidates)
            {
                if (!TryStartBotInteractPath(bot, matchingId, area, info.Id, closureManager)) continue;

                _logger.LogInformation(
                    "Bot recovery explore queued: BotId={Bot}, Stamina={Stamina}, Area={Area}, InteractId={InteractId}",
                    bot.PlayerId, bot.Stamina, area, info.Id);
                return true;
            }
        }

        return false;
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
            mapId,
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
        AreaClosureManager closureManager) =>
        GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() &&
                           !IsAreaClosingOrClosed(closureManager, matchingId, area))
            .ToList();

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

    public bool TrySendBotToInteract(long matchingId, long botPlayerId, AreaType area, int interactId,
        AreaClosureManager closureManager)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null || bot.IsEliminated) return false;
        if (bot.IsInInteraction)
        {
            bot.PendingForcedInteractArea = area;
            bot.PendingForcedInteractId = interactId;
            _logger.LogInformation(
                "봇 상호작용 중 선물 회수 이동 예약: BotId={Bot}, Area={Area}, InteractId={InteractId}",
                bot.PlayerId, area, interactId);
            return false;
        }

        return TryStartBotInteractPath(bot, matchingId, area, interactId, closureManager);
    }

    private bool TryStartBotInteractPath(BotPlayerState bot, long matchingId, AreaType area, int interactId,
        AreaClosureManager closureManager, bool clearInteractQueue = true)
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
            bot.PendingChecklistTaskId = 0;
            bot.PendingChecklistInteractId = 0;
            bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
            if (clearInteractQueue) ClearBotRoomExplorePlan(bot);
            bot.RngCollectProgressStartTime = DateTime.MinValue;
            bot.IsInInteraction = false;
            bot.InteractionStayUntil = DateTime.MinValue;
            bot.PendingForcedInteractArea = AreaType.None;
            bot.PendingForcedInteractId = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.TransitionPauseUntil = DateTime.MinValue;
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
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        if (clearInteractQueue) ClearBotRoomExplorePlan(bot);
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        bot.IsInInteraction = false;
        bot.InteractionStayUntil = DateTime.MinValue;
        bot.PendingForcedInteractArea = AreaType.None;
        bot.PendingForcedInteractId = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.PendingExploreEndBroadcast = true;

        _logger.LogInformation(
            "봇 선물 회수 이동 시작: BotId={Bot}, Area={Area}, InteractId={InteractId}, Steps={Steps}",
            bot.PlayerId, area, interactId, path.Count);
        return true;
    }

    private bool TryStartBotChecklistPath(BotPlayerState bot, long matchingId, AreaType area, int interactId,
        int taskId, AreaClosureManager closureManager)
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
        }
        else
        {
            var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
                area, targetCell,
                a => IsAreaClosingOrClosed(closureManager, matchingId, a));
            if (path == null || path.Count == 0) return false;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.MovementDestination = area;
            bot.PendingExploreEndBroadcast = true;
        }

        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        bot.PendingChecklistTaskId = taskId;
        bot.PendingChecklistInteractId = interactId;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        ClearBotRoomExplorePlan(bot);
        bot.IsInInteraction = false;
        bot.InteractionStayUntil = DateTime.MinValue;
        bot.PendingForcedInteractArea = AreaType.None;
        bot.PendingForcedInteractId = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.TransitionPauseUntil = DateTime.MinValue;
        bot.WalkVelocity = new Vector3f(0f, 0f, 0f);

        _logger.LogInformation(
            "Bot school activity queued: BotId={Bot}, TaskId={TaskId}, Area={Area}, InteractId={InteractId}, Steps={Steps}",
            bot.PlayerId, taskId, area, interactId, bot.Path.Count);
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

