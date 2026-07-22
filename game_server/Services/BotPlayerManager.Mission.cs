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
        InGameInventoryManager inventoryManager, ItemPoolManager itemPoolManager,
        AreaItemStockManager areaItemStockManager, GroundItemManager groundItemManager, ChecklistManager checklistManager)
    {
        var result = new BotMissionTickResult();
        if (!_botStates.TryGetValue(matchingId, out var bots)) return result;

        foreach (var bot in GetActiveBots(bots))
        {
            if ((DateTime.UtcNow - bot.LastMissionTickTime).TotalSeconds < BotMissionTickIntervalSeconds)
                continue;
            bot.LastMissionTickTime = DateTime.UtcNow;

            if (TryAdvanceBotRest(bot, result)) continue;
            TryAutoMergeConsumables(bot, matchingId, inventoryManager, result);
            TryAutoUseConsumable(bot, matchingId, inventoryManager, result);
            if (bot.Stamina <= 0 && TryStartBotRest(bot, result)) continue;
            TryAutoPrepareBattleItem(bot, matchingId, inventoryManager, result);
            // 플레이어와 대화 중일 때는 탐색/선물 회수를 잠시 멈춘다.
            if (bot.IsInInteraction) continue;

            if (Config.CHECKLIST_SYSTEM_ENABLED)
            {
                TryChecklistActivityIfArrived(bot, matchingId, checklistManager, inventoryManager, result);
                if (bot.PendingChecklistTaskId > 0) continue;
            }
            else if (bot.PendingChecklistTaskId > 0 ||
                     bot.PendingChecklistInteractId > 0 ||
                     bot.ChecklistActivityProgressStartTime != DateTime.MinValue)
            {
                ClearPendingChecklistActivity(bot);
            }

            // 실제 지역 루팅은 레거시 부품 미션의 존재/완료 여부와 무관하게 진행한다.
            TryRngCollectIfArrived(bot, matchingId, missionManager, inventoryManager, itemPoolManager,
                areaItemStockManager, groundItemManager, result);

            var state = missionManager.GetState(matchingId, bot.PlayerId);
            if (state == null || state.IsCompleted) continue;

            // 레거시 부품 결합은 해당 미션이 활성 상태일 때만 유지한다.
            TryAutoCombine(bot, matchingId, missionManager, state, result);

            TryAutoUseConsumable(bot, matchingId, inventoryManager, result);
        }

        return result;
    }

    private void TryAutoMergeConsumables(
        BotPlayerState bot,
        long matchingId,
        InGameInventoryManager inventoryManager,
        BotMissionTickResult result)
    {
        var mergedItemIds = BotConsumableLoadout.MergeAvailable(
            inventoryManager,
            matchingId,
            bot.PlayerId);
        foreach (int itemId in mergedItemIds)
        {
            result.ConsumableMerges.Add((bot.PlayerId, itemId));
            _logger.LogInformation(
                "Bot consumable merged: MatchingId={MatchingId}, BotId={BotId}, ItemId={ItemId}",
                matchingId,
                bot.PlayerId,
                itemId);
        }
    }

    /// <summary>봇이 자동 소모품을 사용하기 위한 스태미나 임계값.</summary>
    private void TryAutoPrepareBattleItem(
        BotPlayerState bot,
        long matchingId,
        InGameInventoryManager inventoryManager,
        BotMissionTickResult result)
    {
        int previouslyEquippedItemId = inventoryManager
            .GetEquippedBattleItem(matchingId, bot.PlayerId)?.ItemId ?? 0;
        var loadout = BotBattleItemLoadout.CombineAndEquip(
            inventoryManager,
            matchingId,
            bot.PlayerId,
            _rng);

        foreach (int itemId in loadout.CombinedItemIds)
        {
            result.BattleItemCombines.Add((bot.PlayerId, itemId));
            _logger.LogInformation(
                "Bot battle item combined: MatchingId={MatchingId}, BotId={BotId}, ItemId={ItemId}",
                matchingId,
                bot.PlayerId,
                itemId);
        }

        if (loadout.EquippedItemId == 0 || loadout.EquippedItemId == previouslyEquippedItemId)
            return;

        bot.EquippedBattleItemId = loadout.EquippedItemId;
        result.BattleItemEquips.Add((bot.PlayerId, loadout.EquippedItemId));
        _logger.LogInformation(
            "Bot battle item equipped: MatchingId={MatchingId}, BotId={BotId}, ItemId={ItemId}",
            matchingId,
            bot.PlayerId,
            loadout.EquippedItemId);
    }
    private const int BotAutoConsumableStaminaThreshold = 30;

    /// <summary>봇이 오염 회복품 사용을 검토하는 정신오염도 임계값.</summary>
    private const int BotAutoConsumableCorruptionThreshold = 50;

    /// <summary>봇 자동 소모품 재사용 쿨다운(초).</summary>
    private const int BotAutoConsumableCooldownSeconds = 20;

    /// <summary>봇 채집 시작 시 차감되는 스태미나 (플레이어 RngCollectStaminaCost와 동일).</summary>
    private const int BotRngCollectStaminaCost = 5;

    /// <summary>봇 RNG 채집 progress 지속 시간 (플레이어 클라 2초 progress와 동등).</summary>
    private const double BotRngCollectProgressSeconds = 2.0;

    private const int RngCollectCooldownSeconds = RngCollectCooldownStore.DefaultCooldownSeconds;

    /// <summary>봇 RNG 인스턴스 쿨타임 — RngCollectCore의 동등 상수 (BotPlayerManager 내부 노출용).</summary>
    private const int GiftFoundCorruptionDelta = 30;

    private const int BotCatPillowItemId = 401000003;
    private const int BotCatPillowRestDurationSeconds = 15;
    private const int BotCatPillowRestTickSeconds = 3;
    private const int BotCatPillowStaminaPerTick = 10;

    private void TryChecklistActivityIfArrived(BotPlayerState bot, long matchingId,
        ChecklistManager checklistManager, InGameInventoryManager inventoryManager, BotMissionTickResult result)
    {
        if (bot.PendingChecklistTaskId <= 0 || bot.PendingChecklistInteractId <= 0) return;
        if (bot.Path.Count > 0 && bot.PathIndex < bot.Path.Count) return;

        var task = checklistManager.GetActiveTasks(matchingId, bot.PlayerId)
            .FirstOrDefault(activeTask => activeTask.TaskId == bot.PendingChecklistTaskId);
        var info = GameInteractableData.Get(bot.PendingChecklistInteractId);
        if (task == null || info == null || info.ZoneId != (int)bot.CurrentArea)
        {
            ClearPendingChecklistActivity(bot);
            return;
        }

        var now = DateTime.UtcNow;
        if (bot.ChecklistActivityProgressStartTime == DateTime.MinValue)
        {
            bot.ChecklistActivityProgressStartTime = now;
            ApplyBotStaminaCost(bot, Math.Max(0, task.StaminaCost));
            result.BotExploreStarts.Add((bot.PlayerId, info.Id, bot.CurrentArea));
            result.StartedChecklistActivities.Add((bot.PlayerId, task.TaskId, info.Id, bot.CurrentArea));
            _logger.LogInformation(
                "Bot checklist activity started: BotId={Bot}, TaskId={TaskId}, InteractId={InteractId}, Area={Area}",
                bot.PlayerId, task.TaskId, info.Id, bot.CurrentArea);
            return;
        }

        if ((now - bot.ChecklistActivityProgressStartTime).TotalSeconds < BotRngCollectProgressSeconds) return;

        var completion = checklistManager.TryCompleteTask(
            matchingId,
            bot.PlayerId,
            task.TaskId,
            bot.CurrentArea,
            info.Id,
            inventoryManager);

        result.BotExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
        if (completion.ErrorCode == ErrorCode.SUCCESS)
        {
            result.CompletedChecklistActivities.Add((
                bot.PlayerId,
                task.TaskId,
                info.Id,
                bot.CurrentArea,
                completion.AwardedScore,
                completion.AwardedContribution));
            _logger.LogInformation(
                "Bot checklist activity completed: BotId={Bot}, TaskId={TaskId}, Contribution+{Contribution}",
                bot.PlayerId, task.TaskId, completion.AwardedContribution);
        }
        else
        {
            _logger.LogDebug(
                "Bot checklist activity skipped: BotId={Bot}, TaskId={TaskId}, Error={Error}",
                bot.PlayerId, task.TaskId, completion.ErrorCode);
        }

        ClearPendingChecklistActivity(bot);
    }

    private static void ClearPendingChecklistActivity(BotPlayerState bot)
    {
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.LoopWaitUntil = DateTime.MinValue;
    }

    private bool TryStartBotRest(BotPlayerState bot, BotMissionTickResult result)
    {
        if (bot.Stamina > 0) return false;

        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        ClearPendingChecklistActivity(bot);
        bot.WalkVelocity = new Vector3f(0f, 0f, 0f);
        bot.RestUntil = DateTime.UtcNow.AddSeconds(BotCatPillowRestDurationSeconds);
        bot.NextRestTickAt = DateTime.UtcNow;
        result.BotRestStarts.Add((bot.PlayerId, bot.CurrentArea));

        _logger.LogInformation("Bot zero-stamina rest started: BotId={Bot}", bot.PlayerId);
        return TryAdvanceBotRest(bot, result);
    }

    private static bool TryAdvanceBotRest(BotPlayerState bot, BotMissionTickResult result)
    {
        var now = DateTime.UtcNow;
        if (bot.RestUntil == DateTime.MinValue || now >= bot.RestUntil)
        {
            bool wasResting = bot.RestUntil != DateTime.MinValue;
            bot.RestUntil = DateTime.MinValue;
            bot.NextRestTickAt = DateTime.MinValue;
            if (wasResting)
                result.BotRestEnds.Add((bot.PlayerId, bot.CurrentArea));
            return false;
        }

        if (bot.NextRestTickAt == DateTime.MinValue || now >= bot.NextRestTickAt)
        {
            bot.Stamina = Math.Min(100, bot.Stamina + BotCatPillowStaminaPerTick);
            bot.NextRestTickAt = now.AddSeconds(BotCatPillowRestTickSeconds);
        }

        return true;
    }

    /// <summary>
    ///     #134 — 봇이 walking으로 InteractObject 셀에 도착했을 때 RNG 채집 트리거.
    ///     2단계 흐름:
    ///       (1) 첫 호출: progress 시작 → ExploreStart broadcast (다른 클라가 봇 캐릭터 EXPLORE_1 애니메이션 동기화)
    ///       (2) 1.5초 경과 후: RngCollectCore.Resolve → 결과 산출 + ExploreEnd + 쿨타임 broadcast
    /// </summary>
    private void TryRngCollectIfArrived(BotPlayerState bot, long matchingId,
        MissionManager missionManager, InGameInventoryManager inventoryManager,
        ItemPoolManager itemPoolManager, AreaItemStockManager areaItemStockManager,
        GroundItemManager groundItemManager, BotMissionTickResult result)
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

        if (!areaItemStockManager.HasRemaining(matchingId, info.ZoneId))
        {
            MarkBotRoomExploreComplete(bot);
            bot.PendingRngInteractId = 0;
            bot.RngCollectProgressStartTime = DateTime.MinValue;
            return;
        }
        // 쿨타임 체크 — walking 도중 다른 누군가가 회수한 경우 progress 시작 X. 즉시 다음 InteractObject로 진행.
        // 봇 본인이 1단계 진입 후 cooldown 등록한 경우는 우회 (자기 cooldown).
        if (bot.RngCollectProgressStartTime == DateTime.MinValue &&
            !RngCollectCooldownStore.TryAcquireCooldown(
                matchingId, info.Id, RngCollectCooldownSeconds, out _))
        {
            _logger.LogInformation(
                "Bot RNG skipped (already collected): BotId={Bot}, InteractId={Iid}",
                bot.PlayerId, info.Id);
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
            result.BotExploreStarts.Add((bot.PlayerId, info.Id, bot.CurrentArea));
            if (RngCollectCooldownSeconds > 0)
            {
                result.RngCooldownBroadcasts.Add((info.Id, RngCollectCooldownSeconds));
            }

            _logger.LogInformation(
                "봇 RNG progress 시작: BotId={BotId}, InteractId={Iid}, Area={Area}",
                bot.PlayerId, info.Id, bot.CurrentArea);
            return;
        }

        // (2) progress 진행 중 — 1.5초 미만이면 대기
        if ((now - bot.RngCollectProgressStartTime).TotalSeconds < BotRngCollectProgressSeconds) return;

        // (3) progress 완료 → RNG 결과 산출
        if (TryHandleGiftDiscoveryForBot(bot, matchingId, missionManager, info.Id, result))
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, info.Id);
            result.BotExploreEnds.Add((bot.PlayerId, bot.CurrentArea));
            result.RngCooldownBroadcasts.Add((info.Id, 0));

            bot.PendingRngInteractId = 0;
            bot.RngCollectProgressStartTime = DateTime.MinValue;
            CompleteRoomExploreAttempt(
                bot,
                info.Id,
                bot.EquippedBattleItemId <= 0 && areaItemStockManager.HasRemaining(matchingId, info.ZoneId));
            return;
        }

        var outcome = RngCollectCore.Resolve(matchingId, bot.PlayerId, bot.MyJobTitle,
            info, missionManager, inventoryManager, itemPoolManager, areaItemStockManager, isBot: true,
            bonusItemChancePercent: PassiveBuffUtility.GetValuePercent(
                bot.ActiveBuffIds,
                BuffSubType.ITEM_GAIN_CHANCE_ADD));

        if (outcome.DroppedItemIds.Count > 0)
        {
            float originX = (info.CellX - info.CellY) / 2f;
            float originY = (info.CellX + info.CellY) / 4f;
            result.GroundItemSpawns.AddRange(groundItemManager.SpawnItems(
                matchingId, bot.CurrentArea, originX, originY, outcome.DroppedItemIds,
                discovererPlayerId: bot.PlayerId,
                discovererPickupWindow: GroundItemManager.DiscovererPickupWindow));
        }
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

        bool droppedBattleItem = outcome.DroppedItemIds.Any(BattleItemCombatData.IsCombatItem);
        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        CompleteRoomExploreAttempt(
            bot,
            info.Id,
            bot.EquippedBattleItemId <= 0 &&
            !droppedBattleItem &&
            areaItemStockManager.HasRemaining(matchingId, info.ZoneId));
    }

    private static void CompleteRoomExploreAttempt(BotPlayerState bot, int interactId,
        bool keepExploringCurrentRoom)
    {
        if (interactId > 0)
            bot.ExploredRngInteractIds.Add(interactId);

        bot.LoopWaitUntil = DateTime.MinValue;
        if (keepExploringCurrentRoom && bot.InteractQueueInArea.Count > 0)
            return;

        MarkBotRoomExploreComplete(bot);
    }

    private bool TryHandleGiftDiscoveryForBot(BotPlayerState bot, long matchingId, MissionManager missionManager,
        int interactId, BotMissionTickResult result)
    {
        if (!missionManager.TryDiscoverGift(matchingId, bot.PlayerId, interactId, out var discovery)) return false;
        if (discovery.DiscoveryType == GiftDiscoveryType.Other) return false;

        bot.Corruption = Math.Min(100, bot.Corruption + GiftFoundCorruptionDelta);
        result.GiftDiscoveries.Add(discovery);

        _logger.LogInformation(
            "봇 비밀 선물 발견: BotId={BotId}, Owner={Owner}, InteractId={InteractId}, Corruption={Corruption}",
            bot.PlayerId, discovery.OwnerPlayerId, interactId, bot.Corruption);

        return true;
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
    ///     #134 — 봇 자동 소모품 사용. 스태미나 또는 정신오염도 임계값에 따라 보관 중인 아이템 소비.
    ///     CONDITION_ADD(stamina up) 또는 CORRUPTION_DOWN buff를 즉시 적용.
    ///     CORRUPTION_ADD는 회복 후보에서 제외하되, 실제 아이템 처리 경로가 추가되면 오염 증가 효과로 해석한다.
    /// </summary>
    private void TryAutoUseConsumable(BotPlayerState bot, long matchingId, InGameInventoryManager inventoryManager, BotMissionTickResult result)
    {
        bool needsStamina = bot.Stamina < BotAutoConsumableStaminaThreshold;
        bool needsCorruptionRecovery = bot.Corruption >= BotAutoConsumableCorruptionThreshold;
        if (!needsStamina && !needsCorruptionRecovery) return;
        if ((DateTime.UtcNow - bot.LastAutoConsumableUseTime).TotalSeconds < BotAutoConsumableCooldownSeconds) return;

        var inventory = inventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
        var items = inventory.GetAllItems();
        if (items.Count == 0) return;

        // 현재 부족한 자원에 실제로 기여하는 회복량이 가장 큰 아이템을 선택한다.
        InGameItemInfo? bestItem = null;
        int bestStaminaGain = 0;
        int bestCorruptionDown = 0;

        foreach (var item in items)
        {
            if (item.Count <= 0) continue;
            if (item.ItemId == BotCatPillowItemId) continue;
            var data = GameItemData.Get(item.ItemId);
            if (data == null) continue;
            if (data.ConsumableBuffList.Count == 0) continue;

            int staminaGain = 0;
            int corruptionDown = 0;
            foreach (var (buffId, value, _) in data.ConsumableBuffList)
            {
                var buff = GameBuffData.Get(buffId);
                if (buff == null) continue;
                if (buff.SubType == BuffSubType.CONDITION_ADD)
                    staminaGain += PassiveBuffUtility.ApplyIncrease(
                        value,
                        bot.ActiveBuffIds,
                        BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
                else if (buff.SubType == BuffSubType.CORRUPTION_DOWN)
                    corruptionDown += PassiveBuffUtility.ApplyIncrease(
                        value,
                        bot.ActiveBuffIds,
                        BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
            }

            staminaGain = needsStamina ? Math.Min(100 - bot.Stamina, staminaGain) : 0;
            corruptionDown = needsCorruptionRecovery ? Math.Min(bot.Corruption, corruptionDown) : 0;
            if (staminaGain <= 0 && corruptionDown <= 0) continue;

            int recoveryScore = staminaGain + corruptionDown;
            int bestRecoveryScore = bestStaminaGain + bestCorruptionDown;
            if (recoveryScore > bestRecoveryScore ||
                recoveryScore == bestRecoveryScore && staminaGain > bestStaminaGain)
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
        if (bestCorruptionDown > 0)
            result.CorruptionRecoveries.Add((bot.PlayerId, bestCorruptionDown));
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

        // H4 봇 race 페이스 캡 — legacy mode에서 봇은 7:00 이전 결합 차단 (시연자 race 보장)
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
                result.RaceWinnerPlayerId = bot.PlayerId;
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
    ///     H3+D4: legacy mode 활성화 시 SC 봇이 06:40에 DC를 강제 지목, 그 외 봇 색출은 비활성.
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
    ///     H3 — SC 봇이 06:40 경과 시 DC를 색출 강제 지목 (영상 4컷 비트).
    ///     실제 마니또 관계와 무관하게 target=DC로 고정 — 결과 빗나감은 ManittoChainManager.TryDetect에서 보정.
    /// </summary>
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
    ///     H6 — legacy mode BR 봇이 09:30 시점 도서관에 함정 흔적 1회 배치.
    ///     1회 캡(HasPlacedDemoTrapTrace)으로 영상 09:40 비트 정합. BR 외 직책은 배치 안 함.
    ///     반환: 배치 성공 시 (BR PlayerId, area, interactId, description), 아니면 null.
    /// </summary>
}

/// <summary>
///     봇 미션 틱 결과 — 호출자(GameServer)가 클라이언트 브로드캐스트에 사용.
/// </summary>
public class BotMissionTickResult
{
    public List<(long botPlayerId, int partId)> CollectedParts { get; } = new();
    public List<(long botPlayerId, int shareGroup)> CollectedPrereqs { get; } = new();
    public List<(long botPlayerId, int outputPartId, bool isRaceComplete)> Combined { get; } = new();
    public List<(long botPlayerId, int taskId, int interactId, AreaType area)>
        StartedChecklistActivities
    { get; } = new();

    public List<(long botPlayerId, int taskId, int interactId, AreaType area, float awardedScore, int awardedContribution)>
        CompletedChecklistActivities
    { get; } = new();

    /// <summary>#134 — 봇이 RNG 채집한 InteractObject 인스턴스 쿨타임 broadcast 정보.</summary>
    public List<(int interactId, int cooldownSeconds)> RngCooldownBroadcasts { get; } = new();
    public List<GroundItemInfo> GroundItemSpawns { get; } = new();
    public List<(long botPlayerId, int itemId)> ConsumableMerges { get; } = new();
    public List<(long botPlayerId, int amount)> CorruptionRecoveries { get; } = new();
    public List<(long botPlayerId, int itemId)> BattleItemCombines { get; } = new();
    public List<(long botPlayerId, int itemId)> BattleItemEquips { get; } = new();

    /// <summary>#134 — 봇이 RNG progress 시작했음을 같은 영역 인간 세션에 알림 (G_TO_C_EXPLORE_START).</summary>
    public List<(long botId, int interactId, AreaType area)> BotExploreStarts { get; } = new();

    /// <summary>#134 — 봇이 RNG progress 종료했음을 같은 영역 인간 세션에 알림 (G_TO_C_EXPLORE_END).</summary>
    public List<(long botId, AreaType area)> BotExploreEnds { get; } = new();

    public List<(long botId, AreaType area)> BotRestStarts { get; } = new();
    public List<(long botId, AreaType area)> BotRestEnds { get; } = new();

    /// <summary>봇이 발견한 선물 진행도 — 설치자에게 G_TO_C_GIFT_PROGRESS로 전달.</summary>
    public List<GiftDiscoveryResult> GiftDiscoveries { get; } = new();

    /// <summary>race 완주 PlayerId — 0이면 없음, GameServer가 즉시 게임 종료 처리.</summary>
    public long RaceWinnerPlayerId { get; set; }
}
