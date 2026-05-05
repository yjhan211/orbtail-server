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
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>영역 내 셀 wander 주기 (Phase 2). 실제 플레이어 50ms 대비 봇은 5초로 충분.</summary>
    private const int BotCellWanderIntervalSeconds = 5;

    /// <summary>봇 자원 틱 결과. 자원 고갈 탈락 + 위치 이동 이벤트를 함께 반환.</summary>
    public class BotTickResult
    {
        public List<(long botPlayerId, EliminationReason reason)> Eliminated { get; } = new();
        public List<BotMovementEvent> Movements { get; } = new();
    }

    /// <summary>
    ///     봇 AI 틱(자원 변동 + 이동). GameServer.ProcessResourceTick에서 매칭별로 호출.
    ///     반환: 자원 고갈 탈락 봇 + 위치 이동 이벤트 — 호출자가 패킷 브로드캐스트.
    /// </summary>
    public BotTickResult ProcessBotTick(
        long matchingId, int corruptionDelta, AreaClosureManager areaClosureManager)
    {
        var result = new BotTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            // 1) 오염도 적용 (시한부 추가)
            int totalCorruptionDelta = corruptionDelta;
            if (bot.ManittoStatus == ManittoStatus.TERMINAL)
                totalCorruptionDelta += 5;
            bot.Corruption = Math.Clamp(bot.Corruption + totalCorruptionDelta, 0, 100);

            // H8 — DemoMode HE 봇 12:00 강제 탈락 (오염도 100으로 가속)
            if (DemoMode.IsActive
                && bot.MyJobTitle == JobTitle.HEALTH_MEMBER
                && bot.Corruption < 100)
            {
                var elapsedSec = (DateTime.UtcNow - bot.GameStartTime).TotalSeconds;
                if (elapsedSec >= DemoMode.HeForcedEliminationSeconds)
                {
                    bot.Corruption = 100;
                    _logger.LogInformation(
                        "DEMO_MODE H8: HE 봇 강제 탈락 트리거 (경과 {Sec}s)", (int)elapsedSec);
                }
            }

            // 2) 폐쇄 구역 체류 시 스태미나 감소(가드: 능동 회피 실패 시에만 발생)
            if (areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea))
                bot.Stamina = Math.Max(0, bot.Stamina - 20);

            // 3) 탈락 체크
            if (bot.Stamina <= 0 || bot.Corruption >= 100)
            {
                bot.IsEliminated = true;
                var reason = bot.Stamina <= 0 ? EliminationReason.STAMINA_ZERO : EliminationReason.MENTAL_ZERO;
                _logger.LogInformation("봇 탈락: MatchingId={MatchingId}, BotId={BotId}, 사유={Reason}",
                    matchingId, bot.PlayerId, reason);
                result.Eliminated.Add((bot.PlayerId, reason));
                continue;
            }

            // 4) 영역 이동 — DemoMode 시 W3 스크립트, 아니면 12초 주기 큐 순회
            BotMovementEvent? areaMove = null;
            if (DemoMode.IsActive)
            {
                areaMove = AdvanceToScriptedArea(bot, matchingId, areaClosureManager);
            }
            else if ((DateTime.UtcNow - bot.LastMoveTime).TotalSeconds >= BotMoveIntervalSeconds)
            {
                areaMove = AdvanceToNextArea(bot, matchingId, areaClosureManager);
                bot.LastMoveTime = DateTime.UtcNow;
            }

            if (areaMove != null)
            {
                result.Movements.Add(areaMove);
                continue; // 영역 전환한 틱은 셀 wander 생략
            }

            // 5) Phase 2 — 영역 내 셀 wander (5초 주기, 같은 영역 내 walkable 셀 hop)
            if ((DateTime.UtcNow - bot.LastCellWanderTime).TotalSeconds >= BotCellWanderIntervalSeconds)
            {
                var wander = WanderInArea(bot, matchingId);
                bot.LastCellWanderTime = DateTime.UtcNow;
                if (wander != null) result.Movements.Add(wander);
            }
        }
        return result;
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

        var ev = TransitionBotArea(bot, matchingId, target);
        bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
        bot.LastMoveTime = DateTime.UtcNow;
        _logger.LogInformation("DEMO_MODE 봇 이동(스크립트): BotId={Bot}, Job={Job}, {Prev} → {Area} (경과 {Sec}s)",
            bot.PlayerId, bot.MyJobTitle, ev.FromArea, ev.ToArea, elapsedSec);
        return ev;
    }

    /// <summary>
    ///     다음 이동 결정 — 자기 직책 발견 구역 큐를 우선 순회하며 폐쇄 구역은 능동 회피.
    ///     큐가 비거나 모두 폐쇄되면 폴백으로 열린 구역 무작위 선택.
    /// </summary>
    private BotMovementEvent? AdvanceToNextArea(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        var openAreas = MovableAreas
            .Where(a => !closureManager.IsAreaClosed(matchingId, a))
            .ToHashSet();

        if (openAreas.Count == 0) return null;

        // 1) 큐에서 폐쇄되지 않고 현재 구역과 다른 다음 구역 찾기
        if (bot.JobAreaQueue.Count > 0)
        {
            for (int i = 0; i < bot.JobAreaQueue.Count; i++)
            {
                int idx = (bot.JobAreaQueueIndex + i) % bot.JobAreaQueue.Count;
                var candidate = bot.JobAreaQueue[idx];
                if (!openAreas.Contains(candidate)) continue;
                if (candidate == bot.CurrentArea) continue;

                bot.JobAreaQueueIndex = (idx + 1) % bot.JobAreaQueue.Count;
                bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
                var ev = TransitionBotArea(bot, matchingId, candidate);
                _logger.LogDebug("봇 이동(직책 큐): BotId={BotId}, Job={Job}, → {Area}",
                    bot.PlayerId, bot.MyJobTitle, candidate);
                return ev;
            }
        }

        // 2) 폴백: 열린 구역 무작위 선택 (현재 구역 제외)
        var fallback = openAreas.Where(a => a != bot.CurrentArea).ToList();
        if (fallback.Count == 0) return null;

        var pick = fallback[_rng.Next(fallback.Count)];
        bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
        var fallbackEv = TransitionBotArea(bot, matchingId, pick);
        _logger.LogDebug("봇 이동(폴백 무작위): BotId={BotId}, → {Area}", bot.PlayerId, pick);
        return fallbackEv;
    }

    /// <summary>
    ///     봇 영역 전환 — Cell/Position을 새 영역의 스폰 셀로 갱신하고 BotMovementEvent 생성.
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
    ///     Phase 2 — 영역 내 셀 wander. 같은 영역 안에서 walkable 인근 셀로 1칸 hop.
    ///     실제 플레이어의 C_TO_G_MOVE/G_TO_C_MOVE 흐름 동등 (봇은 5초 주기).
    /// </summary>
    private BotMovementEvent? WanderInArea(BotPlayerState bot, long matchingId)
    {
        var mapId = GetMatchingMapId(matchingId);
        var areaRegion = GameMapData.GetAreas(mapId)
            .FirstOrDefault(r => r.AreaType == bot.CurrentArea);
        if (areaRegion == null) return null;

        // 인근 8방향 + 2칸 hop 후보 — 폐쇄적이지 않게 약간의 거리감
        var deltas = new[]
        {
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (-1, 1), (1, -1), (1, 1),
            (-2, 0), (2, 0), (0, -2), (0, 2)
        };

        var candidates = new List<Cell>();
        foreach (var (dx, dy) in deltas)
        {
            var cand = new Cell(bot.Cell.X + dx, bot.Cell.Y + dy);
            if (!areaRegion.Contains(cand)) continue;
            if (!GameMapData.IsMoveablePosition(mapId, cand)) continue;
            if (cand.X == bot.Cell.X && cand.Y == bot.Cell.Y) continue;
            candidates.Add(cand);
        }

        if (candidates.Count == 0) return null;

        var pick = candidates[_rng.Next(candidates.Count)];
        var fromCell = bot.Cell;
        var fromPosition = bot.Position;
        var newPosition = CellToWorldPosition(pick);

        bot.Cell = pick;
        bot.Position = newPosition;

        return new BotMovementEvent
        {
            BotPlayerId = bot.PlayerId,
            FromArea = bot.CurrentArea,
            ToArea = bot.CurrentArea,
            FromCell = fromCell,
            ToCell = pick,
            Position = newPosition,
            Velocity = new Vector3f(0f, 0f, 0f),
            Rotation = bot.Rotation,
            IsAreaTransition = false
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
///     봇 이동 이벤트. ProcessBotTick이 반환하면 GameServer가 같은 영역 인간 세션에 패킷 브로드캐스트.
///     영역 전환 시: G_TO_C_AREA_PLAYER_LEAVE(이전) + G_TO_C_AREA_PLAYER_ENTER(새) + G_TO_C_MOVE(텔레포트)
///     영역 내 wander 시: G_TO_C_MOVE 만 (같은 영역 인간들에게)
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
