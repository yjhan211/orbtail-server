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

    /// <summary>아이소메트릭 세로 속도 보정 — 클라 PlayerMovement.isoVerticalSpeedScale과 같은 값을 유지해야 한다.</summary>
    private const float IsoVerticalSpeedScale = 1f;

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
        public List<BotOrbFarmingPivot> OrbFarmingPivots { get; } = new();
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
        IReadOnlyCollection<BotCombatTargetSnapshot> combatTargets)
    {
        var result = new BotWalkingTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        // 전체 플레이어(인간 + 봇) 현재 영역 맵 — 봇 타겟 추적/떠보기 인원수 계산용.
        var playerAreas = new Dictionary<long, AreaType>(humanAreas);
        foreach (var b in bots)
            if (!b.IsEliminated) playerAreas[b.PlayerId] = b.CurrentArea;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            // Escape routes outrank looting and combat so a warning cannot be overwritten by a chase path.
            bool isEvacuating = TryMaintainClosureEvacuation(bot, matchingId, closureManager);
            bool isCommittingToDestination = !isEvacuating &&
                                             TryMaintainMovementDestination(bot, matchingId, closureManager);
            if (!isEvacuating &&
                TryAutoPickupGroundItem(bot, matchingId, inventoryManager, groundItemManager, out var pickup) &&
                pickup.HasValue)
            {
                result.GroundItemPickups.Add(pickup.Value);
            }
            if (!isEvacuating && !isCommittingToDestination)
                UpdateCombatMovementIntent(bot, matchingId, closureManager, combatTargets);
            var ev = WalkStep(bot, matchingId, closureManager, areaItemStockManager, playerAreas, checklistManager);
            if (ev != null) result.Movements.Add(ev);
            if (bot.PendingOrbFarmingPivotTo != AreaType.None)
            {
                result.OrbFarmingPivots.Add(new BotOrbFarmingPivot(
                    bot.PlayerId,
                    bot.OrbFarmingTargetColor,
                    bot.PendingOrbFarmingPivotFrom,
                    bot.PendingOrbFarmingPivotTo));
                bot.PendingOrbFarmingPivotFrom = AreaType.None;
                bot.PendingOrbFarmingPivotTo = AreaType.None;
            }
            if (bot.PendingExploreEndBroadcast)
            {
                result.ExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
                bot.PendingExploreEndBroadcast = false;
            }
        }
        return result;
    }

    private bool TryMaintainClosureEvacuation(BotPlayerState bot, long matchingId,
        AreaClosureManager closureManager)
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

            return true;
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

    private int CountAreaPressure(long matchingId, AreaType area)
    {
        return _botStates.TryGetValue(matchingId, out var bots)
            ? bots.Count(other =>
            {
                if (other.IsEliminated)
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
        AreaItemStockManager areaItemStockManager, IReadOnlyDictionary<long, AreaType> playerAreas,
        ChecklistManager checklistManager)
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

            ChooseNewWanderTarget(bot, matchingId, closureManager, areaItemStockManager, playerAreas,
                checklistManager);
            if (bot.Path.Count == 0) return null;
            // ChooseNewWanderTarget이 LoopWaitUntil(+3초)을 설정하므로 새 path는 다음 틱부터 진행.
            // 같은 틱에서 walking 시작 시 영역 도착 후 3초 휴식이 무력화되어 발소리/walk 애니가 끊기지 않음.
            return null;
        }

        var nextStep = bot.Path[bot.PathIndex];
        var mapId = GetMatchingMapId(matchingId);
        var fromArea = bot.CurrentArea;
        var fromCell = bot.Cell;
        bool reachedStep = false;

        // Walk every waypoint at the same speed. An area transition is just the adjacent cell across a door.
        var targetPos = CellToWorldPosition(nextStep.Cell);
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
                var followingPos = CellToWorldPosition(bot.Path[bot.PathIndex].Cell);
                float nextDx = followingPos.X - newPosition.X;
                float nextDy = followingPos.Y - newPosition.Y;
                float nextDist = (float)Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextDist > 0.01f)
                    velocity = ScaledWalkVelocity(nextDx / nextDist, nextDy / nextDist, bot.WindResonanceActive) * GetBotWaveSlowMultiplier(bot);
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
        AreaItemStockManager areaItemStockManager, IReadOnlyDictionary<long, AreaType> playerAreas,
        ChecklistManager checklistManager)
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

    private bool TryStartOrbResonanceFarmingPath(BotPlayerState bot, long matchingId, MapId mapId,
        AreaClosureManager closureManager, AreaItemStockManager areaItemStockManager)
    {
        var color = bot.OrbFarmingTargetColor;
        if (color == SurvivorOrbColor.None)
            return false;

        bool currentAreaHasTargetStock = !bot.CurrentArea.IsCorridor() &&
                                         !IsAreaClosingOrClosed(closureManager, matchingId, bot.CurrentArea) &&
                                         areaItemStockManager.HasRemainingOrbColor(
                                             matchingId, (int)bot.CurrentArea, color);
        bool hasAnyTargetStock = currentAreaHasTargetStock || GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Any(area => area != AreaType.None && !area.IsCorridor() &&
                         !IsAreaClosingOrClosed(closureManager, matchingId, area) &&
                         areaItemStockManager.HasRemainingOrbColor(matchingId, (int)area, color));

        var candidates = GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && area != bot.CurrentArea && !area.IsCorridor() &&
                           !IsRecentCombatRetreatOrigin(bot, area) &&
                           !IsAreaClosingOrClosed(closureManager, matchingId, area) &&
                           areaItemStockManager.HasRemainingOrbColor(matchingId, (int)area, color))
            .Select(area => new
            {
                Area = area,
                Path = BotPathfinder.FindPath(
                    mapId, bot.CurrentArea, bot.Cell, area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, area) ??
                    GameMapData.GetAreaSpawnCell(mapId, area),
                    candidate => IsAreaClosingOrClosed(closureManager, matchingId, candidate))
            })
            .Where(candidate => candidate.Path is { Count: > 0 })
            .OrderBy(candidate => CountAreaPressure(matchingId, candidate.Area))
            .ThenBy(candidate => candidate.Path!.Count)
            .ThenBy(_ => _rng.Next())
            .ToList();

        var destination = candidates.FirstOrDefault();
        if (currentAreaHasTargetStock &&
            (destination == null ||
             CountAreaPressure(matchingId, bot.CurrentArea) <=
             CountAreaPressure(matchingId, destination.Area) + 1))
        {
            bot.OrbFarmingDestination = bot.CurrentArea;
            bot.OrbFarmingPivotPending = false;
            return false;
        }

        if (destination?.Path == null)
        {
            if (!hasAnyTargetStock)
                bot.OrbFarmingTargetColor = SurvivorOrbColor.None;
            bot.OrbFarmingDestination = AreaType.None;
            bot.OrbFarmingPivotPending = false;
            return false;
        }

        AreaType from = bot.CurrentArea;
        bot.Path = destination.Path;
        bot.PathIndex = 0;
        bot.MovementDestination = destination.Area;
        bot.LoopWaitUntil = RandomizedDelayFromNow(0.4, 1.0);
        bot.OrbFarmingDestination = destination.Area;
        if (bot.OrbFarmingPivotPending)
        {
            bot.PendingOrbFarmingPivotFrom = from;
            bot.PendingOrbFarmingPivotTo = destination.Area;
        }
        bot.OrbFarmingPivotPending = false;
        _logger.LogInformation(
            "Bot orb route pivot: MatchingId={MatchingId}, BotId={BotId}, Color={Color}, {From}->{To}, Steps={Steps}",
            matchingId, bot.PlayerId, color, from, destination.Area, destination.Path.Count);
        return true;
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

public sealed record BotOrbFarmingPivot(long BotPlayerId, SurvivorOrbColor Color, AreaType FromArea,
    AreaType ToArea);
