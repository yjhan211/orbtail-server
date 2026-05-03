using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     봇 미션 행동 AI. v0.2.0 부품 회수/결합/사보타주/색출 시뮬.
///     #26: 발견 구역 도착 시 부품 회수 → 4 소재 회수 후 자동 결합 → 최종 결합 시 race 완주.
///     - MissionManager 직접 호출(서버 실측 진행도) — 패킷 송신은 봇이라 생략.
///     - 클라이언트 봇은 음수 PlayerId라 SendXxx 불필요. 서버 상태만 진행시키면 충분.
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>
    ///     봇 미션 틱. 매칭 단위로 game_server에서 주기 호출.
    ///     - 현재 구역에서 부품/선행 회수 시도
    ///     - 4 소재 보유 시 결합 트리거 (M1+M2 → I1, M3+M4 → I2, I1+I2 → F)
    /// </summary>
    public BotMissionTickResult ProcessBotMissionTick(long matchingId, MissionManager missionManager)
    {
        var result = new BotMissionTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in GetActiveBots(bots))
        {
            if ((DateTime.UtcNow - bot.LastMissionTickTime).TotalSeconds < BotMissionTickIntervalSeconds)
                continue;
            bot.LastMissionTickTime = DateTime.UtcNow;

            var state = missionManager.GetState(matchingId, bot.PlayerId);
            if (state == null || state.IsCompleted) continue;

            // 1) 현재 구역에서 부품 회수 시도 (자기 직책 발견 풀 매칭)
            TryCollectAtCurrentArea(bot, matchingId, missionManager, state, result);

            // 2) 결합 시도 (회수 직후 보유 부품 검사)
            TryAutoCombine(bot, matchingId, missionManager, state, result);
        }

        return result;
    }

    /// <summary>
    ///     봇 현재 구역의 자기 직책 부품/선행 아이템 회수 시도.
    ///     mission_step.csv의 (target_area, target_object_type)에 부합하면 회수.
    /// </summary>
    private void TryCollectAtCurrentArea(BotPlayerState bot, long matchingId,
        MissionManager missionManager, PlayerPartState state, BotMissionTickResult result)
    {
        var materials = GameMissionData.GetMaterials((short)bot.MyJobTitle);

        // (a) 현재 구역의 미회수 소재 — 선행 충족 시 회수
        var pendingHere = materials.FirstOrDefault(p =>
            p.TargetArea == (int)bot.CurrentArea &&
            !state.CollectedParts.Contains(p.PartId));

        if (pendingHere != null)
        {
            bool prereqOk = pendingHere.PrerequisiteShareGroup <= 0
                || state.CollectedPrereqGroups.Contains(pendingHere.PrerequisiteShareGroup);

            if (!prereqOk)
            {
                // 선행 미보유 — 선행 위치를 큐 앞으로 끌어오기
                EnqueuePrereqAreaIfMissing(bot, pendingHere.PartId);
            }
            else
            {
                var collect = missionManager.TryCollectPart(matchingId, bot.PlayerId,
                    bot.CurrentArea, pendingHere.TargetObjectType);
                if (collect is { Success: true, Part: not null })
                {
                    bot.Stamina = Math.Min(100, bot.Stamina + collect.StaminaReward);
                    _logger.LogInformation(
                        "봇 부품 회수: BotId={BotId}, Job={Job}, PartId={Part}({Name}), 스태미나+{R}",
                        bot.PlayerId, bot.MyJobTitle, collect.Part.PartId, collect.Part.PartNameKr,
                        collect.StaminaReward);
                    result.CollectedParts.Add((bot.PlayerId, collect.Part.PartId));
                    return;
                }
            }
        }

        // (b) 현재 구역의 자기 직책 선행 아이템 — 자동 회수
        foreach (var part in materials)
        {
            if (part.PrerequisiteShareGroup <= 0) continue;
            if (state.CollectedPrereqGroups.Contains(part.PrerequisiteShareGroup)) continue;

            var prereq = PrerequisiteItemData.GetForPart(part.PartId);
            if (prereq == null) continue;
            if (prereq.LocationArea != (int)bot.CurrentArea) continue;

            bool ok = missionManager.TryCollectPrerequisite(matchingId, bot.PlayerId,
                bot.CurrentArea, prereq.LocationObjectType);
            if (!ok) continue;

            bot.Stamina = Math.Min(100, bot.Stamina + 6); // 선행 보상(GameClientSession.Manitto.cs와 동일)
            _logger.LogInformation(
                "봇 선행 회수: BotId={BotId}, ShareGroup={G} ({Name})",
                bot.PlayerId, prereq.ShareGroup, prereq.ItemNameKr);
            result.CollectedPrereqs.Add((bot.PlayerId, prereq.ShareGroup));
            return;
        }
    }

    /// <summary>
    ///     선행 위치를 봇 동선 큐 다음 위치에 끼워넣기 — 회수가 막힌 부품 처리 우선순위 상향.
    /// </summary>
    private static void EnqueuePrereqAreaIfMissing(BotPlayerState bot, int targetPartId)
    {
        var prereq = PrerequisiteItemData.GetForPart(targetPartId);
        if (prereq == null) return;
        var prereqArea = (AreaType)prereq.LocationArea;
        if (prereq.LocationArea <= 0) return;
        if (bot.JobAreaQueue.Contains(prereqArea))
        {
            // 이미 큐에 있으면 다음 인덱스로 끌어오기 (재이동 시 곧 방문)
            int idx = bot.JobAreaQueue.IndexOf(prereqArea);
            int target = bot.JobAreaQueueIndex % bot.JobAreaQueue.Count;
            if (idx != target)
            {
                (bot.JobAreaQueue[idx], bot.JobAreaQueue[target]) =
                    (bot.JobAreaQueue[target], bot.JobAreaQueue[idx]);
            }
            return;
        }
        bot.JobAreaQueue.Insert(bot.JobAreaQueueIndex % Math.Max(1, bot.JobAreaQueue.Count), prereqArea);
    }

    /// <summary>
    ///     봇 결합 시뮬. PartRecipe 순서: M1+M2 → I1, M3+M4 → I2, I1+I2 → F.
    ///     실제 race 완주(IsRaceComplete=true)는 호출자(GameServer)가 별도 처리하도록 신호만 반환.
    /// </summary>
    private void TryAutoCombine(BotPlayerState bot, long matchingId,
        MissionManager missionManager, PlayerPartState state, BotMissionTickResult result)
    {
        var owned = state.CollectedParts;

        // H4 봇 race 페이스 캡 — DemoMode에서 봇은 7:00 이전 결합 차단 (시연자 race 보장)
        if (DemoMode.IsActive)
        {
            var elapsed = DateTime.UtcNow - bot.GameStartTime;
            if (elapsed.TotalSeconds < DemoMode.BotRaceMinSeconds) return;
        }

        // 가능한 모든 레시피 시도 (PartRecipeData 직접 참조)
        foreach (var recipe in PartRecipeData.GetRecipes((short)bot.MyJobTitle))
        {
            if (state.IsCompleted) break;
            if (!owned.Contains(recipe.InputPartA) || !owned.Contains(recipe.InputPartB)) continue;
            if (owned.Contains(recipe.OutputPart)) continue;

            var combineResult = missionManager.TryCombineParts(
                matchingId, bot.PlayerId, recipe.InputPartA, recipe.InputPartB,
                clientStartUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!combineResult.Success) continue;
            bot.Stamina = Math.Min(100, bot.Stamina + combineResult.StaminaReward);

            _logger.LogInformation(
                "봇 결합: BotId={BotId}, {A}+{B} → {Out} (Race={Race})",
                bot.PlayerId, recipe.InputPartA, recipe.InputPartB, recipe.OutputPart,
                combineResult.IsRaceComplete);

            result.Combined.Add((bot.PlayerId, recipe.OutputPart, combineResult.IsRaceComplete));

            if (combineResult.IsRaceComplete)
                result.RaceWinnerBotId = bot.PlayerId;
        }
    }

    /// <summary>
    ///     시한부 봇 사보타주 시뮬. 자원 충분 시 부품 보유한 다른 생존자(봇/세션 모두) 1명 무효화.
    ///     실제 무효화는 MissionManager.InvalidateHighestPart로 처리.
    ///     호출자(GameServer)가 매칭별로 살아있는 세션 PlayerId 목록을 넘긴다.
    /// </summary>
    public List<(long terminalBotId, long victimPlayerId, int? invalidatedPart)>
        ProcessTerminalSabotage(long matchingId, MissionManager missionManager,
            IReadOnlyList<long> aliveCandidatePlayerIds)
    {
        var result = new List<(long, long, int?)>();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in GetActiveBots(bots))
        {
            if (bot.ManittoStatus != ManittoStatus.TERMINAL) continue;
            if (bot.Stamina < 25) continue; // 사보타주 비용

            // 사보타주 후 재시도 쿨다운 (60초)
            if ((DateTime.UtcNow - bot.LastSabotageTryTime).TotalSeconds < 60) continue;
            bot.LastSabotageTryTime = DateTime.UtcNow;

            // 후보: 자기 자신 제외한 살아있는 PlayerId 중 무작위
            var candidates = aliveCandidatePlayerIds.Where(id => id != bot.PlayerId).ToList();
            if (candidates.Count == 0) continue;

            long victim = candidates[_rng.Next(candidates.Count)];
            int? invalidated = missionManager.InvalidateHighestPart(matchingId, victim);

            // 비용 차감
            bot.Stamina = Math.Max(0, bot.Stamina - 25);
            _logger.LogInformation(
                "봇 사보타주: TerminalBotId={Bot}, Victim={Victim}, InvalidatedPart={Part}",
                bot.PlayerId, victim, invalidated);

            result.Add((bot.PlayerId, victim, invalidated));
        }
        return result;
    }

    /// <summary>
    ///     봇 색출 휴리스틱. 자기 race 진행을 방해하는 흔적 함정 누적 점수가 임계값 초과 시
    ///     자기 마니또(자기를 타겟으로 가진 플레이어) 후보 1명을 색출.
    ///     호출자(GameServer)가 색출 결과를 ManittoChainManager로 위임.
    /// </summary>
    public List<(long detecterBotId, long candidateManittoId)> CollectDetectionAttempts(long matchingId,
        Func<long, long?> findMyManittoForBot)
    {
        var result = new List<(long, long)>();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in GetActiveBots(bots))
        {
            if (bot.HasUsedDetection) continue;
            if (bot.DetectionUrgency < DetectScoreThreshold) continue;
            if (bot.ManittoStatus != ManittoStatus.ACTIVE && bot.ManittoStatus != ManittoStatus.FREED) continue;

            long? candidate = findMyManittoForBot(bot.PlayerId);
            if (!candidate.HasValue) continue;

            bot.HasUsedDetection = true;
            _logger.LogInformation("봇 색출 시도: BotId={Bot}, Candidate={Cand}, Urgency={U}",
                bot.PlayerId, candidate.Value, bot.DetectionUrgency);
            result.Add((bot.PlayerId, candidate.Value));
        }
        return result;
    }

    /// <summary>
    ///     봇 색출 휴리스틱 점수 가산. 호출자가 흔적 발견/타겟 함정 등을 감지했을 때 호출.
    ///     마니또 배치 흔적이 자기 race를 방해할수록 점수 누적.
    /// </summary>
    public void AddDetectionUrgency(long matchingId, long botPlayerId, int delta)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return;
        bot.DetectionUrgency = Math.Max(0, bot.DetectionUrgency + delta);
    }
}

/// <summary>
///     봇 미션 틱 결과 — 호출자(GameServer)가 클라이언트 브로드캐스트에 사용.
/// </summary>
public class BotMissionTickResult
{
    public List<(long botPlayerId, int partId)> CollectedParts { get; } = new();
    public List<(long botPlayerId, int shareGroup)> CollectedPrereqs { get; } = new();
    public List<(long botPlayerId, int outputPartId, bool isRaceComplete)> Combined { get; } = new();

    /// <summary>race 완주 봇 PlayerId — 0이면 없음, GameServer가 즉시 게임 종료 처리.</summary>
    public long RaceWinnerBotId { get; set; }
}
