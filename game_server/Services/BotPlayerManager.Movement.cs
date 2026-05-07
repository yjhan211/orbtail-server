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
    /// <summary>봇 walking 속도 (실제 플레이어 walkSpeed=3과 동일).</summary>
    private const float BotWalkSpeed = 3.0f;

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
        long matchingId, int corruptionDelta, AreaClosureManager areaClosureManager)
    {
        var result = new BotTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            // issue22 디버그: walking 시각 검증을 위해 자원 자연 감소 + 탈락 비활성.
            // DemoMode일 때만 기존 자원/탈락 로직 유지(영상 시나리오 정합).
            if (DemoMode.IsActive)
            {
                // 1) 오염도 적용 (시한부 추가)
                int totalCorruptionDelta = corruptionDelta;
                if (bot.ManittoStatus == ManittoStatus.TERMINAL)
                    totalCorruptionDelta += 5;
                bot.Corruption = Math.Clamp(bot.Corruption + totalCorruptionDelta, 0, 100);

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
                if (areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea))
                    bot.Corruption = Math.Min(100, bot.Corruption + 4);

                // 3) 탈락 체크 — Corruption 100만 트리거 (권고안 B). Stamina 0은 비탈락.
                if (bot.Corruption >= 100)
                {
                    bot.IsEliminated = true;
                    _logger.LogInformation("봇 탈락: MatchingId={MatchingId}, BotId={BotId}, 사유=MENTAL_ZERO",
                        matchingId, bot.PlayerId);
                    result.Eliminated.Add((bot.PlayerId, EliminationReason.MENTAL_ZERO));
                    continue;
                }

                // 4) DemoMode 스크립트 텔레포트 폐기 — 봇은 직책 큐(JobAreaQueue) 따라 walking으로만 이동.
                //    H3/H6/H8 narrative 트리거(색출/흔적/탈락)는 BotPlayerManager.Mission.cs에서 별도 시간 기반 처리.
            }
            // 디버그 모드(DemoMode 비활성): 자원 변동/탈락 모두 스킵 → 봇이 무한 walking
        }
        return result;
    }

    /// <summary>
    ///     #127: 봇 walking 틱(50ms). DemoMode 비활성 시 BotPathfinder 경로를 따라 셀 단위 이동.
    ///     실제 플레이어와 동일한 walkSpeed=3.0 적용. 매 틱 G_TO_C_MOVE 동등 이벤트 발행.
    ///     #134: 추가로 ChooseNewWanderTarget 시 PendingExploreEndBroadcast가 set된 봇은 ExploreEnds list에 수집 — walking 시작 안전망.
    /// </summary>
    public BotWalkingTickResult ProcessBotMovementTick(long matchingId, AreaClosureManager closureManager)
    {
        var result = new BotWalkingTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;
            var ev = WalkStep(bot, matchingId, closureManager);
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
    private BotMovementEvent? WalkStep(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
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

        // issue22 디버그: 도착 후 대기 중이면 walking 스킵
        if (now < bot.LoopWaitUntil) return null;

        // 경로 없거나 완료 → 새 타겟 결정
        if (bot.Path.Count == 0 || bot.PathIndex >= bot.Path.Count)
        {
            // #134 — 도착 후 RNG 채집이 아직 안 됐으면 walking 보류 (ProcessBotMissionTick이 PendingRngInteractId 처리 후 0으로 클리어할 때까지 대기).
            if (bot.PendingRngInteractId != 0) return null;

            ChooseNewWanderTarget(bot, matchingId, closureManager);
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
            velocity = new Vector3f(0f, 0f, 0f);
            bot.PathIndex++;
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
    private const int BotArrivalWaitSeconds = 3;

    /// <summary>
    ///     봇이 도착했거나 경로가 비었을 때 새 목적지 선택 + 경로 계산.
    ///     #134 — 두 모드:
    ///       1) 영역 내 다음 InteractObject로 셀 walking (InteractQueueInArea에 남은 게 있을 때)
    ///       2) 다음 영역으로 walking + 진입 시 그 영역의 모든 후보로 큐 채움
    /// </summary>
    private void ChooseNewWanderTarget(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        var mapId = GetMatchingMapId(matchingId);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.TransitionPauseUntil = DateTime.MinValue;
        bot.PendingRngInteractId = 0;
        // walking 시작 시 EXPLORE_END broadcast 안전망 — 다음 ProcessBotMovementTick에서 수집.
        bot.PendingExploreEndBroadcast = true;

        // 1) 현재 영역에 아직 탐색하지 않은 InteractObject가 남아있으면 같은 영역 내 다음 셀로 walking
        if (TryWalkToNextInteractInQueue(bot, matchingId, mapId, closureManager)) return;

        // 2) 직책 큐의 다음 영역으로 이동
        if (bot.JobAreaQueue.Count == 0) return;

        AreaType targetArea = AreaType.None;
        for (int i = 0; i < bot.JobAreaQueue.Count; i++)
        {
            int idx = (bot.JobAreaQueueIndex + i) % bot.JobAreaQueue.Count;
            var candidate = bot.JobAreaQueue[idx];
            if (closureManager.IsAreaClosed(matchingId, candidate)) continue;
            if (candidate == bot.CurrentArea) continue;
            targetArea = candidate;
            bot.JobAreaQueueIndex = (idx + 1) % bot.JobAreaQueue.Count;
            break;
        }

        if (targetArea == AreaType.None) return;

        // 새 영역의 모든 후보 InteractObject로 큐 채움 (쿨타임 없는 + 셀 좌표 매핑된 것만)
        var queue = BuildInteractQueueForArea(matchingId, bot, targetArea);
        bot.InteractQueueInArea = queue;

        // 도착 후 잠시 대기 (자연스러운 휴식 + ProcessBotMissionTick이 RNG 채집 트리거할 시간)
        bot.LoopWaitUntil = DateTime.UtcNow.AddSeconds(BotArrivalWaitSeconds);

        // 첫 InteractObject 셀로 path 계산. 큐 비었으면 영역 spawn cell로 폴백.
        Cell targetCell;
        if (queue.Count > 0)
        {
            var firstId = queue[0];
            queue.RemoveAt(0);
            var info = GameInteractableData.Get(firstId);
            if (info != null)
            {
                targetCell = new Cell(info.CellX, info.CellY);
                bot.PendingRngInteractId = firstId;
            }
            else
            {
                targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, targetArea)
                    ?? GameMapData.GetAreaSpawnCell(mapId, targetArea);
            }
        }
        else
        {
            targetCell = GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, targetArea)
                ?? GameMapData.GetAreaSpawnCell(mapId, targetArea);
        }

        var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
            targetArea, targetCell,
            a => closureManager.IsAreaClosed(matchingId, a));
        if (path == null || path.Count == 0)
        {
            _logger.LogDebug("봇 경로 계산 실패: BotId={Bot}, {From} → {To}",
                bot.PlayerId, bot.CurrentArea, targetArea);
            bot.PendingRngInteractId = 0;
            return;
        }

        bot.Path = path;
        bot.PathIndex = 0;
        _logger.LogInformation(
            "봇 새 경로(직책 큐): BotId={Bot}, Job={Job}, {From}@{Cell} → {To}@{TargetCell}, InteractId={Iid}, 영역내큐={QSize}, 단계={Steps}",
            bot.PlayerId, bot.MyJobTitle, bot.CurrentArea, bot.Cell, targetArea, targetCell,
            bot.PendingRngInteractId, queue.Count, path.Count);
    }

    /// <summary>
    ///     #134 — 같은 영역 내에 아직 탐색하지 않은 InteractObject가 큐에 남아있으면 그 셀까지 walking.
    ///     큐에서 첫 번째 ID를 꺼내 PendingRngInteractId로 설정. 쿨타임 발생한 항목은 건너뛰고 다음.
    ///     반환 true면 같은 영역 walking path가 설정됨. false면 큐 소진 — 호출자가 다음 영역으로 진행.
    /// </summary>
    private bool TryWalkToNextInteractInQueue(BotPlayerState bot, long matchingId, MapId mapId,
        AreaClosureManager closureManager)
    {
        while (bot.InteractQueueInArea.Count > 0)
        {
            int nextId = bot.InteractQueueInArea[0];
            bot.InteractQueueInArea.RemoveAt(0);

            var info = GameInteractableData.Get(nextId);
            if (info == null) continue;
            if (info.ZoneId != (int)bot.CurrentArea) continue;
            if (info.CellX == 0 && info.CellY == 0) continue;
            if (RngCollectCooldownStore.IsInCooldown(matchingId, nextId, out _)) continue;

            var targetCell = new Cell(info.CellX, info.CellY);
            var path = BotPathfinder.FindPath(mapId, bot.CurrentArea, bot.Cell,
                bot.CurrentArea, targetCell,
                a => closureManager.IsAreaClosed(matchingId, a));
            if (path == null || path.Count == 0) continue;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.PendingRngInteractId = nextId;
            // walking 시작 시 EXPLORE_END broadcast 안전망 — 봇이 RNG progress 끝나고 같은 영역 내 다음 셀로 이동 시 EXPLORE_1 잔존 회피.
            bot.PendingExploreEndBroadcast = true;
            // 짧은 대기 — 클라가 walking 시작 직전 잠시 멈춤
            bot.LoopWaitUntil = DateTime.UtcNow.AddSeconds(1);
            _logger.LogInformation(
                "봇 영역내 다음 InteractObject: BotId={Bot}, Area={Area}, InteractId={Iid}@{Cell}, 큐잔량={Q}",
                bot.PlayerId, bot.CurrentArea, nextId, targetCell, bot.InteractQueueInArea.Count);
            return true;
        }
        return false;
    }

    /// <summary>
    ///     #134 — 새 영역 진입 시 그 영역의 모든 InteractObject 후보를 큐로 빌드.
    ///     자기 직책 부품 발견 풀 InteractObject가 큐 앞쪽 (탐색 동기 부여), 그 외는 뒤쪽.
    ///     쿨타임 없는 것 + 셀 좌표 매핑된 것만 후보.
    /// </summary>
    private List<int> BuildInteractQueueForArea(long matchingId, BotPlayerState bot, AreaType targetArea)
    {
        var candidates = GameInteractableData.GetByZone((int)targetArea);
        if (candidates.Count == 0) return new List<int>();

        var materials = GameMissionData.GetMaterials((short)bot.MyJobTitle);
        var jobObjectTypes = materials
            .Where(p => p.TargetArea == (int)targetArea)
            .Select(p => p.TargetObjectType)
            .ToHashSet();

        int unmappedCount = candidates.Count(c => c.CellX == 0 && c.CellY == 0);
        int cooldownCount = candidates.Count(c =>
            (c.CellX != 0 || c.CellY != 0) && RngCollectCooldownStore.IsInCooldown(matchingId, c.Id, out _));

        var available = candidates
            .Where(c => c.CellX != 0 || c.CellY != 0)
            .Where(c => !RngCollectCooldownStore.IsInCooldown(matchingId, c.Id, out _))
            .ToList();

        // 자기 직책 풀 매칭 셔플 → 그 외 셔플
        var preferred = available
            .Where(c => jobObjectTypes.Contains((int)c.ObjectType))
            .OrderBy(_ => _rng.Next())
            .Select(c => c.Id);
        var others = available
            .Where(c => !jobObjectTypes.Contains((int)c.ObjectType))
            .OrderBy(_ => _rng.Next())
            .Select(c => c.Id);

        var queue = preferred.Concat(others).ToList();
        if (unmappedCount > 0 || cooldownCount > 0)
        {
            _logger.LogInformation(
                "봇 영역 큐 빌드: BotId={Bot}, Area={Area}, 전체={Total}, 큐에추가={Q}, 좌표미매핑={Unmapped}, 쿨타임={Cd}",
                bot.PlayerId, targetArea, candidates.Count, queue.Count, unmappedCount, cooldownCount);
        }
        return queue;
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

    /// <summary>
    ///     직책별 발견 구역 + 선행 아이템 위치 큐 생성.
    ///     mission_step.csv의 자기 직책 Material 4개 TargetArea + prerequisite_item 위치를
    ///     중복 제거 후 셔플하여 봇 동선이 직책 단서로 작동하도록 한다(블러프 메카닉 정합).
    /// </summary>
    private List<AreaType> BuildJobAreaQueue(JobTitle job)
    {
        var areas = new HashSet<AreaType>();

        var materials = GameMissionData.GetMaterials((short)job);
        foreach (var part in materials)
        {
            if (part.TargetArea > 0) areas.Add((AreaType)part.TargetArea);
            if (part.PrerequisiteShareGroup <= 0) continue;
            var prereq = PrerequisiteItemData.GetForPart(part.PartId);
            if (prereq == null) continue;
            if (prereq.LocationArea > 0) areas.Add((AreaType)prereq.LocationArea);
        }

        return areas.OrderBy(_ => _rng.Next()).ToList();
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
