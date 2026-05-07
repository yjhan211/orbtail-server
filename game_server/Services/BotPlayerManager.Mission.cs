using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

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
    ///     #134 — RNG 채집 통합. 봇이 InteractObject 셀에 도착하면 RNG 결과 산출 + 인스턴스 쿨타임 broadcast.
    ///     - 도착한 InteractObject에서 RNG 채집 (RngCollectCore 공통 로직)
    ///     - 4 소재 보유 시 결합 트리거 (M1+M2 → I1, M3+M4 → I2, I1+I2 → F)
    ///     - 스태미나 부족 시 자동 소모품 사용
    /// </summary>
    public BotMissionTickResult ProcessBotMissionTick(long matchingId, MissionManager missionManager,
        InGameInventoryManager inventoryManager, ItemPoolManager itemPoolManager)
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

            // 1) 봇 walking 도착 후 RNG 채집 (PendingRngInteractId가 있을 때만)
            TryRngCollectIfArrived(bot, matchingId, missionManager, inventoryManager, itemPoolManager, state, result);

            // 2) 결합 시도 (회수 직후 보유 부품 검사)
            TryAutoCombine(bot, matchingId, missionManager, state, result);

            // 3) 스태미나 부족 시 자동 소모품 사용 (인벤토리에 회복 아이템 있을 때)
            TryAutoUseConsumable(bot, matchingId, inventoryManager);
        }

        return result;
    }

    /// <summary>봇이 자동 소모품을 사용하기 위한 스태미나 임계값.</summary>
    private const int BotAutoConsumableStaminaThreshold = 30;

    /// <summary>봇 자동 소모품 재사용 쿨다운(초).</summary>
    private const int BotAutoConsumableCooldownSeconds = 20;

    /// <summary>봇 채집 시작 시 차감되는 스태미나 (플레이어 RngCollectStaminaCost와 동일).</summary>
    private const int BotRngCollectStaminaCost = 5;

    /// <summary>봇 RNG 채집 progress 지속 시간 (플레이어 클라 2초 progress와 동등).</summary>
    private const double BotRngCollectProgressSeconds = 2.0;

    /// <summary>봇 RNG 인스턴스 쿨타임 — RngCollectCore의 동등 상수 (BotPlayerManager 내부 노출용).</summary>
    private const int RngCollectCooldownSeconds = 30;

    /// <summary>
    ///     #134 — 봇이 walking으로 InteractObject 셀에 도착했을 때 RNG 채집 트리거.
    ///     2단계 흐름:
    ///       (1) 첫 호출: progress 시작 → ExploreStart broadcast (다른 클라가 봇 캐릭터 EXPLORE_1 애니메이션 동기화)
    ///       (2) 1.5초 경과 후: RngCollectCore.Resolve → 결과 산출 + ExploreEnd + 쿨타임 broadcast
    /// </summary>
    private void TryRngCollectIfArrived(BotPlayerState bot, long matchingId,
        MissionManager missionManager, InGameInventoryManager inventoryManager,
        ItemPoolManager itemPoolManager, PlayerPartState state, BotMissionTickResult result)
    {
        if (bot.PendingRngInteractId <= 0) return;

        // 도착 판정: walking path가 비어있고 (도달 완료) 영역이 InteractObject 영역과 동일
        if (bot.Path.Count > 0 && bot.PathIndex < bot.Path.Count) return;

        var info = GameInteractableData.Get(bot.PendingRngInteractId);
        if (info == null)
        {
            SkipPendingInteract(bot, matchingId, bot.PendingRngInteractId, result);
            return;
        }
        if (info.ZoneId != (int)bot.CurrentArea)
        {
            SkipPendingInteract(bot, matchingId, info.Id, result);
            return;
        }

        // 쿨타임 체크 — walking 도중 다른 누군가가 회수한 경우 progress 시작 X. 즉시 다음 InteractObject로 진행.
        if (RngCollectCooldownStore.IsInCooldown(matchingId, info.Id, out _))
        {
            _logger.LogInformation(
                "봇 RNG 스킵(이미 회수됨): BotId={Bot}, InteractId={Iid}",
                bot.PlayerId, info.Id);
            // 다른 봇/플레이어가 등록한 cooldown — clear 안 함.
            bot.PendingRngInteractId = 0;
            bot.RngCollectProgressStartTime = DateTime.MinValue;
            bot.LoopWaitUntil = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;

        // (1) 첫 진입: progress 시작 → ExploreStart broadcast + 마커 즉시 숨김
        if (bot.RngCollectProgressStartTime == DateTime.MinValue)
        {
            bot.RngCollectProgressStartTime = now;
            ApplyBotStaminaCost(bot, BotRngCollectStaminaCost);

            // 1단계 cooldown 30초 등록 — 정상 완료 시 동일하게 갱신, 폐기 시 SkipPendingInteract가 clear broadcast로 해제.
            RngCollectCooldownStore.SetCooldown(matchingId, info.Id, RngCollectCooldownSeconds);

            result.BotExploreStarts.Add((bot.PlayerId, info.Id, bot.CurrentArea));
            result.RngCooldownBroadcasts.Add((info.Id, RngCollectCooldownSeconds));

            _logger.LogInformation(
                "봇 RNG progress 시작: BotId={BotId}, InteractId={Iid}, Area={Area}",
                bot.PlayerId, info.Id, bot.CurrentArea);
            return;
        }

        // (2) progress 진행 중 — 1.5초 미만이면 대기
        if ((now - bot.RngCollectProgressStartTime).TotalSeconds < BotRngCollectProgressSeconds) return;

        // (3) progress 완료 → RNG 결과 산출
        var outcome = RngCollectCore.Resolve(matchingId, bot.PlayerId, bot.MyJobTitle,
            info, missionManager, inventoryManager, itemPoolManager, isBot: true);

        if (outcome is { ResultType: 3, CollectedPart: not null })
        {
            bot.Stamina = Math.Min(100, bot.Stamina + outcome.StaminaReward);
            _logger.LogInformation(
                "봇 RNG 부품 회수: BotId={BotId}, Job={Job}, InteractId={Iid}, PartId={Part}({Name})",
                bot.PlayerId, bot.MyJobTitle, info.Id, outcome.CollectedPart.PartId,
                outcome.CollectedPart.PartNameKr);
            result.CollectedParts.Add((bot.PlayerId, outcome.CollectedPart.PartId));
        }
        else
        {
            _logger.LogInformation(
                "봇 RNG 결과: BotId={BotId}, InteractId={Iid}, ResultType={Type}, Item={Item}",
                bot.PlayerId, info.Id, outcome.ResultType, outcome.ItemId);
        }

        // ExploreEnd + 쿨타임 broadcast (다른 클라가 봇 EXPLORE_1 → IDLE 복귀 + 마커 30초 숨김)
        result.BotExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
        result.RngCooldownBroadcasts.Add((info.Id, outcome.CooldownSeconds));

        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
    }

    /// <summary>
    ///     #134 — RNG progress 폐기 정리. 1단계 진입 후 폐기(영역 어긋남/InteractId 미존재 등) 시
    ///     봇이 자기가 등록한 cooldown clear + clear broadcast(cooldownSeconds=0)로 마커 복원 + ExploreEnd.
    ///     LoopWaitUntil도 reset해서 walking 보류 없이 즉시 다음 ChooseNewWanderTarget.
    /// </summary>
    private static void SkipPendingInteract(BotPlayerState bot, long matchingId, int interactId,
        BotMissionTickResult result)
    {
        bool wasInProgress = bot.RngCollectProgressStartTime != DateTime.MinValue;
        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        bot.LoopWaitUntil = DateTime.MinValue;

        // 1단계 진입했었다면 자기가 등록한 cooldown 해제 + 클라 마커 복원 broadcast.
        if (wasInProgress && interactId > 0)
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, interactId);
            result.RngCooldownBroadcasts.Add((interactId, 0));
            result.BotExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
        }
    }

    /// <summary>
    ///     봇 스태미나 차감 — 부족분만큼 corruption 1:2 변환 (플레이어 ModifyStats 동등).
    /// </summary>
    private static void ApplyBotStaminaCost(BotPlayerState bot, int cost)
    {
        int newStamina = bot.Stamina - cost;
        if (newStamina < 0)
        {
            int deficit = -newStamina;
            bot.Stamina = 0;
            bot.Corruption = Math.Min(100, bot.Corruption + deficit * 2);
        }
        else
        {
            bot.Stamina = newStamina;
        }
    }

    /// <summary>
    ///     #134 — 봇 자동 소모품 사용. Stamina < 임계값일 때 인벤토리 회복 아이템 소비.
    ///     CONDITION_ADD(stamina up) 또는 CORRUPTION_DOWN buff를 즉시 적용.
    /// </summary>
    private void TryAutoUseConsumable(BotPlayerState bot, long matchingId, InGameInventoryManager inventoryManager)
    {
        if (bot.Stamina >= BotAutoConsumableStaminaThreshold) return;
        if ((DateTime.UtcNow - bot.LastAutoConsumableUseTime).TotalSeconds < BotAutoConsumableCooldownSeconds) return;

        var inventory = inventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
        var items = inventory.GetAllItems();
        if (items.Count == 0) return;

        // 가장 효율 높은 회복 아이템 선택 — CONDITION_ADD value 합 기준
        InGameItemInfo? bestItem = null;
        int bestStaminaGain = 0;
        int bestCorruptionDown = 0;

        foreach (var item in items)
        {
            if (item.Count <= 0) continue;
            var data = GameItemData.Get(item.ItemId);
            if (data == null) continue;
            if (data.ConsumableBuffList.Count == 0) continue;

            int staminaGain = 0;
            int corruptionDown = 0;
            foreach (var (buffId, value, _) in data.ConsumableBuffList)
            {
                var buff = GameBuffData.Get(buffId);
                if (buff == null) continue;
                if (buff.SubType == BuffSubType.CONDITION_ADD) staminaGain += value;
                else if (buff.SubType == BuffSubType.CORRUPTION_DOWN) corruptionDown += value;
            }

            if (staminaGain <= 0 && corruptionDown <= 0) continue;

            if (staminaGain > bestStaminaGain || (staminaGain == bestStaminaGain && corruptionDown > bestCorruptionDown))
            {
                bestItem = item;
                bestStaminaGain = staminaGain;
                bestCorruptionDown = corruptionDown;
            }
        }

        if (bestItem == null) return;

        if (!inventoryManager.TryRemoveItem(matchingId, bot.PlayerId, bestItem.ItemUid, 1, out _)) return;

        bot.Stamina = Math.Min(100, bot.Stamina + bestStaminaGain);
        bot.Corruption = Math.Max(0, bot.Corruption - bestCorruptionDown);
        bot.LastAutoConsumableUseTime = DateTime.UtcNow;

        _logger.LogInformation(
            "봇 자동 소모품 사용: BotId={Bot}, ItemId={Iid}, Stamina+{S}, Cor-{C}, → Stamina={NS}, Cor={NC}",
            bot.PlayerId, bestItem.ItemId, bestStaminaGain, bestCorruptionDown, bot.Stamina, bot.Corruption);
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
    ///     H3+D4: DemoMode 활성화 시 SC 봇이 06:40에 DC를 강제 지목, 그 외 봇 색출은 비활성.
    /// </summary>
    public List<(long detecterBotId, long candidateManittoId)> CollectDetectionAttempts(long matchingId,
        Func<long, long?> findMyManittoForBot)
    {
        var result = new List<(long, long)>();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        if (DemoMode.IsActive)
        {
            TryAddDemoForcedDetection(bots, result);
            return result; // D4: 시연 모드에서는 강제 트리거(SC→DC) 외 봇 색출 비활성
        }

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
    ///     H3 — SC 봇이 06:40 경과 시 DC를 색출 강제 지목 (영상 4컷 비트).
    ///     실제 마니또 관계와 무관하게 target=DC로 고정 — 결과 빗나감은 ManittoChainManager.TryDetect에서 보정.
    /// </summary>
    private void TryAddDemoForcedDetection(List<BotPlayerState> bots, List<(long, long)> result)
    {
        var sc = bots.FirstOrDefault(b => b.MyJobTitle == JobTitle.SCIENCE_MEMBER
            && !b.IsEliminated && !b.HasUsedDetection);
        if (sc == null) return;

        var elapsed = DateTime.UtcNow - sc.GameStartTime;
        if (elapsed.TotalSeconds < DemoMode.ScDetectionAttemptSeconds) return;

        var dc = bots.FirstOrDefault(b => b.MyJobTitle == JobTitle.DISCIPLINE_MEMBER && !b.IsEliminated);
        if (dc == null)
        {
            _logger.LogWarning("DEMO_MODE 색출 강제: DC 봇이 없어 SC 강제 색출 스킵");
            return;
        }

        sc.HasUsedDetection = true;
        _logger.LogInformation("DEMO_MODE 색출 강제: SC({Sc}) → DC({Dc}) (경과 {S}s)",
            sc.PlayerId, dc.PlayerId, (int)elapsed.TotalSeconds);
        result.Add((sc.PlayerId, dc.PlayerId));
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

    /// <summary>
    ///     H6 — DEMO_MODE BR 봇이 09:30 시점 도서관에 함정 흔적 1회 배치.
    ///     1회 캡(HasPlacedDemoTrapTrace)으로 영상 09:40 비트 정합. BR 외 직책은 배치 안 함.
    ///     반환: 배치 성공 시 (BR PlayerId, area, interactId, description), 아니면 null.
    /// </summary>
    public (long brPlayerId, AreaType area, int interactId, string description)? ProcessDemoBotTracePlacement(
        long matchingId, TraceManager traceManager)
    {
        if (!DemoMode.IsActive) return null;
        if (!_botStates.TryGetValue(matchingId, out var bots)) return null;

        var br = bots.FirstOrDefault(b =>
            b.MyJobTitle == JobTitle.BROADCAST_MEMBER
            && !b.IsEliminated
            && !b.HasPlacedDemoTrapTrace);
        if (br == null) return null;

        var elapsed = DateTime.UtcNow - br.GameStartTime;
        if (elapsed.TotalSeconds < DemoMode.BrTracePlacementSeconds) return null;

        // 동선 스크립트상 BR이 09:30에 도서관에 있어야 정합. 다른 곳이면 보류 (다음 틱 재시도).
        if (br.CurrentArea != DemoMode.BrTraceArea) return null;

        traceManager.AddTrace(matchingId, DemoMode.BrTraceArea, DemoMode.BrTraceInteractId,
            DemoMode.BrTraceDescription, br.PlayerId, isMissionTrace: false);
        br.HasPlacedDemoTrapTrace = true;
        br.LastTracePlaceTime = DateTime.UtcNow;

        _logger.LogInformation(
            "DEMO_MODE H6: BR 봇 함정 흔적 배치 (BotId={Bot}, Area={Area}, InteractId={Iid}, 경과 {Sec}s)",
            br.PlayerId, DemoMode.BrTraceArea, DemoMode.BrTraceInteractId, (int)elapsed.TotalSeconds);

        return (br.PlayerId, DemoMode.BrTraceArea, DemoMode.BrTraceInteractId, DemoMode.BrTraceDescription);
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

    /// <summary>#134 — 봇이 RNG 채집한 InteractObject 인스턴스 쿨타임 broadcast 정보.</summary>
    public List<(int interactId, int cooldownSeconds)> RngCooldownBroadcasts { get; } = new();

    /// <summary>#134 — 봇이 RNG progress 시작했음을 같은 영역 인간 세션에 알림 (G_TO_C_EXPLORE_START).</summary>
    public List<(long botId, int interactId, AreaType area)> BotExploreStarts { get; } = new();

    /// <summary>#134 — 봇이 RNG progress 종료했음을 같은 영역 인간 세션에 알림 (G_TO_C_EXPLORE_END).</summary>
    public List<(long botId, AreaType area)> BotExploreEnds { get; } = new();

    /// <summary>race 완주 봇 PlayerId — 0이면 없음, GameServer가 즉시 게임 종료 처리.</summary>
    public long RaceWinnerBotId { get; set; }
}
