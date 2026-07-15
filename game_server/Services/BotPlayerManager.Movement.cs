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
    private const float BotWalkSpeed = 6.0f;

    /// <summary>영역 전환 직전 도어 앞에서 잠시 멈추는 시간(ms). 포탈 들어가는 시각적 단서.</summary>
    private const int BotTransitionPauseMs = 600;

    /// <summary>봇 자원 틱 결과. 자원 고갈 탈락 + 위치 이동 이벤트(DemoMode 영역 전환만)를 함께 반환.</summary>
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
    }

    /// <summary>
    ///     봇 자원 틱(5초). 자원 변동 + 탈락 + DemoMode 스크립트 영역 전환만 처리.
    ///     일반 walking은 ProcessBotMovementTick(250ms)에서 별도 처리.
    /// </summary>
    public BotTickResult ProcessBotTick(
        long matchingId,
        int isolationCorruptionDelta,
        int nearbyRecoveryDelta,
        int proximityRecoveryDelta,
        int closedAreaCorruptionDelta,
        AreaClosureManager areaClosureManager,
        IReadOnlyList<BotBehaviorPlayerSnapshot> humanSnapshots)
    {
        var result = new BotTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        var allPlayerSnapshots = new List<BotBehaviorPlayerSnapshot>(humanSnapshots);
        foreach (var b in bots)
        {
            if (b.IsEliminated) continue;
            allPlayerSnapshots.Add(new BotBehaviorPlayerSnapshot
            {
                PlayerId = b.PlayerId,
                TargetPlayerId = b.TargetPlayerId,
                CurrentArea = b.CurrentArea,
                Position = b.Position,
                IsEliminated = false
            });
        }

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            bool isTerminal = bot.ManittoStatus == ManittoStatus.TERMINAL;
            int totalCorruptionDelta = 0;

            if (!isTerminal && bot.CurrentArea != AreaType.None)
            {
                var target = allPlayerSnapshots.FirstOrDefault(p =>
                    p.PlayerId == bot.TargetPlayerId && !p.IsEliminated);
                bool targetInSameArea = target != null && target.CurrentArea == bot.CurrentArea;

                if (!targetInSameArea)
                {
                    totalCorruptionDelta += PassiveBuffUtility.ApplyReduction(
                        isolationCorruptionDelta,
                        bot.ActiveBuffIds,
                        BuffSubType.ISOLATION_CORRUPTION_GAIN_DOWN);
                }
                else
                {
                    int population = CountPlayerSnapshotsInArea(allPlayerSnapshots, bot.CurrentArea);
                    int recoveryMagnitude = Math.Max(1,
                        (int)Math.Round(Math.Abs(nearbyRecoveryDelta) * (2.0 / Math.Max(2, population))));
                    totalCorruptionDelta += nearbyRecoveryDelta < 0 ? -recoveryMagnitude : recoveryMagnitude;

                    if (IsBotTargetWithinProximity(bot, target))
                        totalCorruptionDelta += proximityRecoveryDelta;
                }

                if (areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea))
                    totalCorruptionDelta += closedAreaCorruptionDelta;
            }

            // issue22 디버그: walking 시각 검증을 위해 자원 자연 감소 + 탈락 비활성.
            // DemoMode일 때만 기존 자원/탈락 로직 유지(영상 시나리오 정합).
            if (totalCorruptionDelta != 0)
                bot.Corruption = Math.Clamp(bot.Corruption + totalCorruptionDelta, 0, 100);


            if (DemoMode.IsActive)
            {
                // 1) 오염도 적용 (시한부 추가)
                // General resource deltas are accumulated outside DemoMode.
                if (bot.ManittoStatus == ManittoStatus.TERMINAL)
                    bot.Corruption = Math.Clamp(bot.Corruption + 5, 0, 100);

                // H8 — DemoMode HE 봇 12:00 강제 탈락 (오염도 100으로 가속)
                if (bot.MyJobTitle == JobTitle.HEALTH_MEMBER && bot.Corruption < 100)
                {
                    var elapsedSec = (DateTime.UtcNow - bot.GameStartTime).TotalSeconds;
                    if (elapsedSec >= DemoMode.HeForcedEliminationSeconds)
                    {
                        bot.Corruption = 100;
                        _logger.LogInformation(
                            "DEMO_MODE H8: HE 봇 강제 탈락 트리거 (경과 {Sec}s)", (int)elapsedSec);
                    }
                }

                // 2) 폐쇄 구역 체류 시 오염도 가속 (권고안 B 2026-05-05 — 변별력 보강 +4/틱).
                //    이전엔 stamina -20이었으나 자원 통합 후 stamina 0이어도 탈락 안 되므로 cor로 변경.
                // 3) Corruption 100: 정신력 소모로 탈락. Stamina 0은 비탈락.
                // 4) DemoMode 스크립트 텔레포트 폐기 — 봇은 직책 큐(JobAreaQueue) 따라 walking으로만 이동.
                //    H3/H6/H8 narrative 트리거(색출/흔적/탈락)는 BotPlayerManager.Mission.cs에서 별도 시간 기반 처리.
            }

            if (TryQueueBotMentalElimination(bot, matchingId, result))
                continue;

            // DemoMode 비활성에서도 일반 자원/상태 변동 후 오염도 100이면 탈락 처리한다.
        }
        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;
            SyncBotForcedFollowState(bot, matchingId);
        }

        return result;
    }

    private bool TryQueueBotMentalElimination(BotPlayerState bot, long matchingId, BotTickResult result)
    {
        if (bot.Corruption < 100) return false;

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

    private static int CountPlayerSnapshotsInArea(
        IReadOnlyList<BotBehaviorPlayerSnapshot> players,
        AreaType area)
    {
        return players.Count(p => !p.IsEliminated && p.CurrentArea == area);
    }

    private static bool IsBotTargetWithinProximity(BotPlayerState bot, BotBehaviorPlayerSnapshot? target)
    {
        var targetPosition = target?.Position;
        if (targetPosition == null) return false;

        float dx = bot.Position.X - targetPosition.X;
        float dy = bot.Position.Y - targetPosition.Y;
        return dx * dx + dy * dy <=
               Config.TARGET_PROXIMITY_DISTANCE * Config.TARGET_PROXIMITY_DISTANCE;
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
    ///     #127: 봇 walking 틱(50ms). DemoMode 비활성 시 BotPathfinder 경로를 따라 셀 단위 이동.
    ///     Uses the same fixed movement speed 6.0 as the player and emits an equivalent G_TO_C_MOVE event each tick.
    ///     #134: 추가로 ChooseNewWanderTarget 시 PendingExploreEndBroadcast가 set된 봇은 ExploreEnds list에 수집 — walking 시작 안전망.
    /// </summary>
    public BotWalkingTickResult ProcessBotMovementTick(long matchingId, AreaClosureManager closureManager,
        IReadOnlyDictionary<long, AreaType> humanAreas, ChecklistManager checklistManager)
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
            var ev = WalkStep(bot, matchingId, closureManager, playerAreas, checklistManager);
            if (ev != null) result.Movements.Add(ev);
            if (bot.PendingExploreEndBroadcast)
            {
                result.ExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
                bot.PendingExploreEndBroadcast = false;
            }
        }
        return result;
    }

    /// <summary>
    ///     봇 한 명의 walking step 처리. 경로가 없으면 새 wander 타겟 선택.
    ///     영역 전환 단계는 텔레포트(LEAVE+ENTER+MOVE) 이벤트 반환,
    ///     일반 셀 walk는 진행 방향 + 속도 포함 MOVE 이벤트 반환.
    /// </summary>
    private BotMovementEvent? WalkStep(BotPlayerState bot, long matchingId, AreaClosureManager closureManager,
        IReadOnlyDictionary<long, AreaType> playerAreas, ChecklistManager checklistManager)
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

            ChooseNewWanderTarget(bot, matchingId, closureManager, playerAreas, checklistManager);
            if (bot.Path.Count == 0) return null;
            // ChooseNewWanderTarget이 LoopWaitUntil(+3초)을 설정하므로 새 path는 다음 틱부터 진행.
            // 같은 틱에서 walking 시작 시 영역 도착 후 3초 휴식이 무력화되어 발소리/walk 애니가 끊기지 않음.
            return null;
        }

        var nextStep = bot.Path[bot.PathIndex];

        // 1) 영역 경계 통과 — 도어 앞 짧은 멈춤 후 텔레포트 (포탈 들어가는 시각적 단서)
        if (nextStep.IsAreaTransition)
        {
            // 첫 진입: 멈춤 시각 설정 + velocity 0 정지 이벤트 발행
            // (클라가 발소리/walk 애니를 즉시 정지하도록 명시 알림 — 미발행 시 LateUpdate 0.3초 timeout까지 발소리 잔존)
            if (bot.TransitionPauseUntil == DateTime.MinValue)
            {
                bot.TransitionPauseUntil = now.AddMilliseconds(BotTransitionPauseMs);
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

            // 멈춤 진행 중: 패킷 발행 없이 대기
            if (now < bot.TransitionPauseUntil) return null;

            // 멈춤 종료 → 실제 영역 전환
            bot.TransitionPauseUntil = DateTime.MinValue;

            var fromArea = bot.CurrentArea;
            var fromCell = bot.Cell;
            bot.CurrentArea = nextStep.Area;
            bot.Cell = nextStep.Cell;
            bot.Position = CellToWorldPosition(nextStep.Cell);
            bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
            bot.PathIndex++;
            ClearBotRoomExplorePlan(bot);
            return new BotMovementEvent
            {
                BotPlayerId = bot.PlayerId,
                FromArea = fromArea,
                ToArea = bot.CurrentArea,
                FromCell = fromCell,
                ToCell = bot.Cell,
                Position = bot.Position,
                Velocity = new Vector3f(0f, 0f, 0f),
                Rotation = bot.Rotation,
                IsAreaTransition = true
            };
        }

        // 2) 일반 셀 walk — walkSpeed × deltaSec 만큼 진행
        var targetPos = CellToWorldPosition(nextStep.Cell);
        float dx = targetPos.X - bot.Position.X;
        float dy = targetPos.Y - bot.Position.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float maxDist = BotWalkSpeed * deltaSec;

        Vector3f newPosition;
        Vector3f velocity;

        if (dist <= maxDist || dist < 0.01f)
        {
            // 도달 → 다음 인덱스
            newPosition = targetPos;
            bot.Cell = nextStep.Cell;
            bot.Position = newPosition;
            bot.PathIndex++;
            velocity = new Vector3f(0f, 0f, 0f);
            if (bot.PathIndex < bot.Path.Count && !bot.Path[bot.PathIndex].IsAreaTransition)
            {
                var followingPos = CellToWorldPosition(bot.Path[bot.PathIndex].Cell);
                float nextDx = followingPos.X - newPosition.X;
                float nextDy = followingPos.Y - newPosition.Y;
                float nextDist = (float)Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextDist > 0.01f)
                    velocity = new Vector3f(
                        nextDx / nextDist * BotWalkSpeed,
                        nextDy / nextDist * BotWalkSpeed,
                        0f);
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
            velocity = new Vector3f(dirX * BotWalkSpeed, dirY * BotWalkSpeed, 0f);
            bot.Position = newPosition;
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
            FromArea = bot.CurrentArea,
            ToArea = bot.CurrentArea,
            FromCell = bot.Cell,
            ToCell = nextStep.Cell,
            Position = newPosition,
            Velocity = velocity,
            Rotation = bot.Rotation,
            IsAreaTransition = false
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
        IReadOnlyDictionary<long, AreaType> playerAreas, ChecklistManager checklistManager)
    {
        var mapId = GetMatchingMapId(matchingId);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.TransitionPauseUntil = DateTime.MinValue;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        // walking 시작 시 EXPLORE_END broadcast 안전망 — 다음 ProcessBotMovementTick에서 수집.
        bot.PendingExploreEndBroadcast = true;

        if (bot.IsForcedFollowActive &&
            TryStartBotForcedFollowPath(bot, matchingId, mapId, playerAreas))
        {
            return;
        }

        var activeSchoolTask = checklistManager.GetNextActiveGeneralInteractTask(matchingId, bot.PlayerId);
        int schoolTaskCost = activeSchoolTask != null ? Math.Max(0, activeSchoolTask.StaminaCost) : 0;
        if (activeSchoolTask != null &&
            bot.Stamina >= schoolTaskCost &&
            TryStartBotChecklistTaskPath(bot, matchingId, activeSchoolTask, closureManager))
        {
            return;
        }

        if (bot.Stamina < BotAutoConsumableStaminaThreshold &&
            TryStartRecoveryRngPath(bot, matchingId, closureManager))
        {
            return;
        }

        if (TryStartQueuedRoomExplore(bot, matchingId, closureManager))
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
            a => closureManager.IsAreaClosed(matchingId, a));
        if (path == null || path.Count == 0)
        {
            bot.LoopWaitUntil = RandomizedDelayFromNow(0.8, 1.6);
            _logger.LogDebug("프로토0 봇 경로 실패: BotId={Bot}, {From} → {To}",
                bot.PlayerId, bot.CurrentArea, destination);
            return;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        bot.LoopWaitUntil = RandomizedDelayFromNow(BotArrivalWaitMinSeconds, BotArrivalWaitMaxSeconds);
        _logger.LogInformation(
            "Proto0 bot move: BotId={Bot}, Target={Target}, Policy={Policy}, Profile={Profile}, {From}->{To}, Steps={Steps}",
            bot.PlayerId, bot.TargetPlayerId, ActiveProto0BotPolicy, bot.Proto0Profile,
            bot.CurrentArea, destination, path.Count);
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
        if (area == AreaType.None || closureManager.IsAreaClosed(matchingId, area)) return false;

        var info = GameInteractableData.Get(task.InteractId);
        if (info == null || info.ZoneId != task.AreaType) return false;
        if (info.CellX == 0 && info.CellY == 0) return false;

        return TryStartBotChecklistPath(bot, matchingId, area, task.InteractId, task.TaskId, closureManager);
    }

    private bool TryStartQueuedRoomExplore(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        if (bot.CurrentArea == AreaType.None || bot.CurrentArea.IsCorridor() ||
            closureManager.IsAreaClosed(matchingId, bot.CurrentArea))
        {
            ClearBotRoomExplorePlan(bot);
            return false;
        }

        if (bot.CompletedRoomExploreArea == bot.CurrentArea)
            return false;

        if (bot.RoomExploreQueueArea != AreaType.None && bot.RoomExploreQueueArea != bot.CurrentArea)
        {
            bot.InteractQueueInArea.Clear();
            bot.RoomExploreQueueArea = AreaType.None;
        }

        if (bot.InteractQueueInArea.Count == 0 && !RefillRoomExploreQueue(bot))
            return false;

        while (bot.InteractQueueInArea.Count > 0)
        {
            int interactId = bot.InteractQueueInArea[0];
            bot.InteractQueueInArea.RemoveAt(0);

            var info = GameInteractableData.Get(interactId);
            if (!IsBotRoomExploreCandidate(info, bot.CurrentArea))
                continue;

            if (!TryStartBotInteractPath(bot, matchingId, bot.CurrentArea, interactId, closureManager,
                    clearInteractQueue: false))
                continue;

            _logger.LogInformation(
                "Bot room explore queued: BotId={Bot}, Area={Area}, InteractId={InteractId}, Remaining={Remaining}",
                bot.PlayerId, bot.CurrentArea, interactId, bot.InteractQueueInArea.Count);
            return true;
        }

        bot.CompletedRoomExploreArea = bot.CurrentArea;
        bot.RoomExploreQueueArea = AreaType.None;
        return false;
    }

    private bool RefillRoomExploreQueue(BotPlayerState bot)
    {
        var candidates = GameInteractableData.GetByZone((int)bot.CurrentArea)
            .Where(info => IsBotRoomExploreCandidate(info, bot.CurrentArea))
            .OrderBy(_ => _rng.Next())
            .Select(info => info.Id)
            .ToList();

        if (candidates.Count == 0)
        {
            bot.CompletedRoomExploreArea = bot.CurrentArea;
            return false;
        }

        bot.InteractQueueInArea.Clear();
        bot.InteractQueueInArea.AddRange(candidates);
        bot.RoomExploreQueueArea = bot.CurrentArea;
        return true;
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
        bot.CompletedRoomExploreArea = AreaType.None;
    }

    private bool TryStartRecoveryRngPath(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        var areaOrder = new List<AreaType>();
        if (bot.CurrentArea != AreaType.None && !closureManager.IsAreaClosed(matchingId, bot.CurrentArea))
            areaOrder.Add(bot.CurrentArea);

        areaOrder.AddRange(Proto0Rooms
            .Where(area => area != bot.CurrentArea && !closureManager.IsAreaClosed(matchingId, area))
            .OrderBy(_ => _rng.Next()));

        foreach (var area in areaOrder)
        {
            var candidates = GameInteractableData.GetByZone((int)area)
                .Where(info => info.CellX != 0 || info.CellY != 0)
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
    private AreaType ChooseProto0Destination(BotPlayerState bot, long matchingId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        return ActiveProto0BotPolicy switch
        {
            Proto0BotPolicy.DisguiseMvp => ChooseDisguiseProto0Destination(bot, matchingId, playerAreas, closureManager),
            _ => ChooseSimpleProto0Destination(bot, matchingId, playerAreas, closureManager)
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
            area => closureManager.IsAreaClosed(matchingId, area));

        if (decision.Kind == BotBehaviorActionKind.FollowTarget
            && decision.TargetArea != AreaType.None
            && decision.TargetArea != bot.CurrentArea)
            return decision.TargetArea;

        return ChooseProto0Destination(bot, matchingId, playerAreas, closureManager);
    }

    private AreaType ChooseSimpleProto0Destination(BotPlayerState bot, long matchingId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        var rooms = Proto0Rooms
            .Where(a => !closureManager.IsAreaClosed(matchingId, a))
            .ToList();
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
    private AreaType ChooseDisguiseProto0Destination(BotPlayerState bot, long matchingId,
        IReadOnlyDictionary<long, AreaType> playerAreas, AreaClosureManager closureManager)
    {
        var rooms = Proto0Rooms
            .Where(a => !closureManager.IsAreaClosed(matchingId, a))
            .ToList();
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
        if (closureManager.IsAreaClosed(matchingId, area)) return false;

        var info = GameInteractableData.Get(interactId);
        if (info == null || info.ZoneId != (int)area) return false;
        if (info.CellX == 0 && info.CellY == 0) return false;

        var mapId = GetMatchingMapId(matchingId);
        var targetCell = new Cell(info.CellX, info.CellY);
        if (bot.CurrentArea == area && bot.Cell.Equals(targetCell))
        {
            bot.Path.Clear();
            bot.PathIndex = 0;
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
            a => closureManager.IsAreaClosed(matchingId, a));
        if (path == null || path.Count == 0) return false;

        bot.Path = path;
        bot.PathIndex = 0;
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
        if (closureManager.IsAreaClosed(matchingId, area)) return false;

        var info = GameInteractableData.Get(interactId);
        if (info == null || info.ZoneId != (int)area) return false;
        if (info.CellX == 0 && info.CellY == 0) return false;

        var mapId = GetMatchingMapId(matchingId);
        var targetCell = new Cell(info.CellX, info.CellY);
        if (bot.CurrentArea == area && bot.Cell.Equals(targetCell))
        {
            bot.Path.Clear();
            bot.PathIndex = 0;
        }
        else
        {
            var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
                area, targetCell,
                a => closureManager.IsAreaClosed(matchingId, a));
            if (path == null || path.Count == 0) return false;

            bot.Path = path;
            bot.PathIndex = 0;
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
    private BotMovementEvent? AdvanceToScriptedArea(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        if (!DemoMode.BotMovementScript.TryGetValue(bot.MyJobTitle, out var script) || script.Count == 0)
            return null;

        int elapsedSec = (int)(DateTime.UtcNow - bot.GameStartTime).TotalSeconds;
        AreaType target = script[0].area;
        foreach (var (sec, area) in script)
        {
            if (sec > elapsedSec) break;
            target = area;
        }

        if (bot.CurrentArea == target) return null;
        if (closureManager.IsAreaClosed(matchingId, target)) return null;

        // walking 중이면 텔레포트 보류 — 봇이 복도 중앙 등에서 갑자기 사라지는 시각 부자연스러움 회피.
        // 도착 후 LoopWaitUntil 시점에 평가되어 자연스럽게 텔레포트.
        if (bot.Path.Count > 0 && bot.PathIndex < bot.Path.Count) return null;

        var ev = TransitionBotArea(bot, matchingId, target);
        bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
        bot.LastMoveTime = DateTime.UtcNow;
        _logger.LogInformation("DEMO_MODE 봇 이동(스크립트): BotId={Bot}, Job={Job}, {Prev} → {Area} (경과 {Sec}s)",
            bot.PlayerId, bot.MyJobTitle, ev.FromArea, ev.ToArea, elapsedSec);
        return ev;
    }

    /// <summary>
    ///     봇 영역 전환 — Cell/Position을 새 영역의 스폰 셀로 갱신하고 BotMovementEvent 생성.
    ///     DemoMode 스크립트 텔레포트 전용. walking 경로 통과 시점은 WalkStep에서 처리.
    /// </summary>
    private BotMovementEvent TransitionBotArea(BotPlayerState bot, long matchingId, AreaType targetArea)
    {
        var fromArea = bot.CurrentArea;
        var fromCell = bot.Cell;
        var mapId = GetMatchingMapId(matchingId);
        var newCell = GameMapData.GetAreaSpawnCell(mapId, targetArea);
        var newPosition = CellToWorldPosition(newCell);

        bot.CurrentArea = targetArea;
        bot.Cell = newCell;
        bot.Position = newPosition;
        bot.Path.Clear();
        bot.PathIndex = 0;
        ClearBotRoomExplorePlan(bot);

        return new BotMovementEvent
        {
            BotPlayerId = bot.PlayerId,
            FromArea = fromArea,
            ToArea = targetArea,
            FromCell = fromCell,
            ToCell = newCell,
            Position = newPosition,
            Velocity = new Vector3f(0f, 0f, 0f),
            Rotation = bot.Rotation,
            IsAreaTransition = true
        };
    }

}

/// <summary>
///     봇 이동 이벤트. ProcessBotTick / ProcessBotMovementTick이 반환하면 GameServer가 같은 영역 인간 세션에 패킷 브로드캐스트.
///     영역 전환 시: G_TO_C_AREA_PLAYER_LEAVE(이전) + G_TO_C_AREA_PLAYER_ENTER(새) + G_TO_C_MOVE(텔레포트)
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
