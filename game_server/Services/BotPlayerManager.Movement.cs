using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.helpers;

namespace game_server.services;

/// <summary>
///     봇 이동 AI. 자기 직책 발견 구역 우선 순회 + 폐쇄 회피.
///     #26: "허수아비" 무작위 이동 → 직책별 목적성 동선으로 폴리싱.
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>
    ///     봇 AI 틱(자원 변동 + 이동). GameServer.ProcessResourceTick에서 매칭별로 호출.
    ///     반환: 이번 틱에 자원 고갈로 탈락한 봇 목록 — 호출자가 ManittoChainManager에 체인 단절 알림.
    /// </summary>
    public List<(long botPlayerId, EliminationReason reason)> ProcessBotTick(
        long matchingId, int corruptionDelta, AreaClosureManager areaClosureManager)
    {
        var newlyEliminated = new List<(long, EliminationReason)>();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return newlyEliminated;

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
                newlyEliminated.Add((bot.PlayerId, reason));
                continue;
            }

            // 4) 이동 — DemoMode 시 W3 스크립트, 아니면 12초 주기 큐 순회
            if (DemoMode.IsActive)
            {
                AdvanceToScriptedArea(bot, matchingId, areaClosureManager);
            }
            else if ((DateTime.UtcNow - bot.LastMoveTime).TotalSeconds >= BotMoveIntervalSeconds)
            {
                AdvanceToNextArea(bot, matchingId, areaClosureManager);
                bot.LastMoveTime = DateTime.UtcNow;
            }
        }
        return newlyEliminated;
    }

    /// <summary>
    ///     W3 시연 모드 — 봇 위치를 BotMovementScript에 따라 강제. 매 틱(5초)마다 평가.
    ///     큐 순회 로직 우회. 폐쇄된 위치는 도착 보류(다음 웨이포인트로 진행되면 자연 해소).
    /// </summary>
    private void AdvanceToScriptedArea(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        if (!DemoMode.BotMovementScript.TryGetValue(bot.MyJobTitle, out var script) || script.Count == 0)
            return;

        int elapsedSec = (int)(DateTime.UtcNow - bot.GameStartTime).TotalSeconds;
        AreaType target = script[0].area;
        foreach (var (sec, area) in script)
        {
            if (sec > elapsedSec) break;
            target = area;
        }

        if (bot.CurrentArea == target) return;
        if (closureManager.IsAreaClosed(matchingId, target)) return; // 폐쇄면 보류

        var prev = bot.CurrentArea;
        bot.CurrentArea = target;
        bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
        bot.LastMoveTime = DateTime.UtcNow;
        _logger.LogInformation("DEMO_MODE 봇 이동(스크립트): BotId={Bot}, Job={Job}, {Prev} → {Area} (경과 {Sec}s)",
            bot.PlayerId, bot.MyJobTitle, prev, target, elapsedSec);
    }

    /// <summary>
    ///     다음 이동 결정 — 자기 직책 발견 구역 큐를 우선 순회하며 폐쇄 구역은 능동 회피.
    ///     큐가 비거나 모두 폐쇄되면 폴백으로 열린 구역 무작위 선택.
    /// </summary>
    private void AdvanceToNextArea(BotPlayerState bot, long matchingId, AreaClosureManager closureManager)
    {
        var openAreas = MovableAreas
            .Where(a => !closureManager.IsAreaClosed(matchingId, a))
            .ToHashSet();

        if (openAreas.Count == 0) return;

        // 1) 큐에서 폐쇄되지 않고 현재 구역과 다른 다음 구역 찾기
        if (bot.JobAreaQueue.Count > 0)
        {
            for (int i = 0; i < bot.JobAreaQueue.Count; i++)
            {
                int idx = (bot.JobAreaQueueIndex + i) % bot.JobAreaQueue.Count;
                var candidate = bot.JobAreaQueue[idx];
                if (!openAreas.Contains(candidate)) continue;
                if (candidate == bot.CurrentArea) continue;

                bot.CurrentArea = candidate;
                bot.JobAreaQueueIndex = (idx + 1) % bot.JobAreaQueue.Count;
                bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
                _logger.LogDebug("봇 이동(직책 큐): BotId={BotId}, Job={Job}, → {Area}",
                    bot.PlayerId, bot.MyJobTitle, candidate);
                return;
            }
        }

        // 2) 폴백: 열린 구역 무작위 선택 (현재 구역 제외)
        var fallback = openAreas.Where(a => a != bot.CurrentArea).ToList();
        if (fallback.Count == 0) return;

        bot.CurrentArea = fallback[_rng.Next(fallback.Count)];
        bot.Stamina = Math.Max(0, bot.Stamina - BotMoveStaminaCost);
        _logger.LogDebug("봇 이동(폴백 무작위): BotId={BotId}, → {Area}", bot.PlayerId, bot.CurrentArea);
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
