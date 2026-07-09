using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     RNG 채집 공통 로직. 봇/플레이어 모두 동일한 분포를 사용.
///     - 자기 직책 풀: 50/15/25/10 (선행 미사용 직책은 50/0/40/10)
///     - 자기 풀 외: 60/40 (소모품 — object_action.csv reward_pool / 빈손)
///     - 결과 텍스트는 패킷에 담지 않음 — 클라가 InteractId로 csv 조회.
///     - 쿨타임 등록까지 본 메서드가 처리. 패킷 송신은 호출자 책임.
/// </summary>
public static class RngCollectCore
{
    private const int FailStaminaReward = 0;
    private const int AttendanceBlackboardInteractId = 701000055;
    private const int TornAttendancePagePartId = 308;
    private const int BlackedOutPaperPartId = 309;
    // 소모품 회수 stamina 보상 제거 (#135) — 회복은 아이템 사용 시점에만.
    private const int ConsumableStaminaReward = 0;
    private const int MergeDropBaseWeight = 10;
    private const int MergeDropStarterBonus = 4;
    private const int MergeDropMissingIngredientBonus = 18;
    private const int MergeDropProgressBonus = 12;
    private const int MergeDropCompleteRecipeBonus = 40;
    private const int MergeDropDuplicatePenalty = 4;
    private const int MergeDropRecipeBonusCap = 120;
    // Regular explore should only grant consumables. Mission parts are collected through explicit mission actions.
    private const bool AllowMissionPartDropsFromExplore = false;
    private static readonly Random _rng = new();

    public static RngCollectOutcome Resolve(
        long matchingId,
        long playerId,
        JobTitle jobTitle,
        InteractableInfoData info,
        MissionManager missionManager,
        InGameInventoryManager inventoryManager,
        ItemPoolManager itemPoolManager,
        AreaItemStockManager areaItemStockManager,
        bool isBot,
        int bonusItemChancePercent = 0)
    {
        var outcome = new RngCollectOutcome();

        // 자기 풀 매칭은 영역(area) 단위 (#135) — object_type 무시. 한 영역에 자기 부품 1개씩 배치된다는 가정.
        var materials = GameMissionData.GetMaterials((short)jobTitle);
        var matchedParts = materials.Where(p =>
            p.TargetArea == info.ZoneId &&
            (p.TargetObjectType == 0 || p.TargetObjectType == (int)info.ObjectType))
            .ToList();

        // 자기 부품 이미 회수했으면 그 영역은 자기 풀 외 분기(영역 풀 소모품)로 처리 (#135).
        var state = AllowMissionPartDropsFromExplore && matchedParts.Count > 0
            ? missionManager.GetState(matchingId, playerId)
            : null;
        var matchedPart = AllowMissionPartDropsFromExplore
            ? MissionPartSelection.SelectNextCollectablePart(matchedParts, state)
            : null;

        if (matchedPart != null)
        {
            int roll = _rng.Next(100);
            bool hasPrerequisite = matchedPart.PrerequisiteShareGroup > 0;
            bool missingPrerequisite = hasPrerequisite &&
                                       state != null &&
                                       !state.CollectedPrereqGroups.Contains(matchedPart.PrerequisiteShareGroup);
            int missionPartDropRate = DemoMode.IsActive && !missingPrerequisite ? 100 : 90;

            // 자기 풀: 90% 부품 / 7% 선행(있을 때) / 디코이/빈손 — 시연 시간 내 회수 가능하도록 상향 (#135)
            if (roll < missionPartDropRate)
            {
                var collectResult = missionManager.TryCollectPart(matchingId, playerId,
                    (AreaType)info.ZoneId, (int)info.ObjectType, info.Id);
                if (collectResult is { Success: true, Part: not null })
                {
                    outcome.ResultType = 3;
                    outcome.ItemId = collectResult.Part.PartId;
                    outcome.StaminaReward = 0; // 부품 회수 stamina 보상 제거 (#135)
                    outcome.CollectedPart = collectResult.Part;
                    outcome.CompletedMissionNodeIds = collectResult.CompletedMissionNodeIds;

                    // #135 — 부품을 인벤토리에 추가 (본체/충전재 ID 대역 분리)
                    int partItemId = GameMissionData.GetPartItemId(collectResult.Part.PartId);
                    if (partItemId > 0)
                        outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, partItemId, 1);

                    TryGrantAttendanceBlackboardPair(
                        matchingId,
                        playerId,
                        info,
                        collectResult.Part.PartId,
                        missionManager,
                        inventoryManager,
                        outcome,
                        isBot);
                }
                else
                {
                    outcome.ResultType = 1; // 디코이 폴백
                }
            }
            else if (roll < 97 && hasPrerequisite)
            {
                outcome.ResultType = 4;
                outcome.ItemId = matchedPart.PrerequisiteShareGroup;
            }
            else if (roll < 99)
            {
                outcome.ResultType = 1; // 디코이
            }
            else
            {
                outcome.ResultType = 0; // 빈손
            }
        }
        else
        {
            if (TryResolveAreaStockDrop(matchingId, info.ZoneId, areaItemStockManager, consumeStock: !isBot, out int itemId))
            {
                outcome.ResultType = 2;
                outcome.ItemId = itemId;
                outcome.StaminaReward = ConsumableStaminaReward;
                outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, itemId, 1);
            }
            else
            {
                outcome.ResultType = 0;
            }
        }

        TryApplySharpObservationBonus(matchingId, playerId, info.ZoneId, missionManager, inventoryManager,
            areaItemStockManager, isBot, outcome);
        TryApplyPassiveItemGainBonus(
            matchingId,
            playerId,
            info.ZoneId,
            inventoryManager,
            areaItemStockManager,
            isBot,
            outcome,
            bonusItemChancePercent);

        RngCollectCooldownStore.ClearCooldown(matchingId, info.Id);
        outcome.CooldownSeconds = 0;

        return outcome;
    }

    private static void TryApplyPassiveItemGainBonus(
        long matchingId,
        long playerId,
        int areaType,
        InGameInventoryManager inventoryManager,
        AreaItemStockManager areaItemStockManager,
        bool isBot,
        RngCollectOutcome outcome,
        int bonusItemChancePercent)
    {
        if (bonusItemChancePercent <= 0 || outcome.BonusItemId != 0)
            return;

        lock (_rng)
        {
            if (!PassiveBuffUtility.RollPercent(bonusItemChancePercent, _rng))
                return;
        }

        if (!TryResolveAreaStockDrop(matchingId, areaType, areaItemStockManager, consumeStock: !isBot, out int bonusItemId))
            return;

        outcome.BonusItemId = bonusItemId;
        outcome.AddedBonusInventoryItem = inventoryManager.AddItem(matchingId, playerId, bonusItemId, 1);
    }

    private static void TryGrantAttendanceBlackboardPair(
        long matchingId,
        long playerId,
        InteractableInfoData info,
        int collectedPartId,
        MissionManager missionManager,
        InGameInventoryManager inventoryManager,
        RngCollectOutcome outcome,
        bool isBot)
    {
        if (isBot ||
            info.Id != AttendanceBlackboardInteractId ||
            collectedPartId != TornAttendancePagePartId)
        {
            return;
        }

        var extraResult = missionManager.TryCollectPartById(matchingId, playerId, BlackedOutPaperPartId);
        if (extraResult is not { Success: true, Part: not null }) return;

        extraResult.StaminaReward = 0;
        outcome.ExtraCollectedParts.Add(extraResult);

        int extraItemId = GameMissionData.GetPartItemId(extraResult.Part.PartId);
        if (extraItemId > 0)
        {
            var extraInventoryItem = inventoryManager.AddItem(matchingId, playerId, extraItemId, 1);
            if (extraInventoryItem != null)
                outcome.AddedExtraInventoryItems.Add(extraInventoryItem);
        }
    }

    private static void TryApplySharpObservationBonus(
        long matchingId,
        long playerId,
        int areaType,
        MissionManager missionManager,
        InGameInventoryManager inventoryManager,
        AreaItemStockManager areaItemStockManager,
        bool isBot,
        RngCollectOutcome outcome)
    {
        if (!missionManager.TryConsumeShortRewardUse(
                matchingId,
                playerId,
                MissionShortRewardType.SharpObservation,
                out var reward) ||
            reward == null)
            return;

        if (_rng.Next(100) >= reward.ValuePercent)
            return;

        if (!TryResolveAreaStockDrop(matchingId, areaType, areaItemStockManager, consumeStock: !isBot, out int bonusItemId))
            return;

        outcome.BonusItemId = bonusItemId;
        outcome.AddedBonusInventoryItem = inventoryManager.AddItem(matchingId, playerId, bonusItemId, 1);
    }

    private static bool TryResolveAreaStockDrop(
        long matchingId,
        int areaType,
        AreaItemStockManager areaItemStockManager,
        bool consumeStock,
        out int itemId)
    {
        itemId = 0;
        if (consumeStock)
            return areaItemStockManager.TryConsumeDrop(matchingId, areaType, out itemId);

        if (_rng.Next(100) >= 90)
            return false;

        var areaPool = GetAllowedRngItemPool(areaType);
        if (areaPool.Count == 0)
            return false;

        itemId = areaPool[_rng.Next(areaPool.Count)];
        return itemId > 0;
    }
    private static int SelectMergePuzzleDropItem(
        List<int> areaPool,
        IReadOnlyCollection<InGameItemInfo> inventoryItems)
    {
        if (areaPool.Count == 0) return 0;

        var ownedCounts = inventoryItems?
            .Where(item => item.Count > 0)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count))
            ?? new Dictionary<int, int>();

        var weightedItems = areaPool
            .GroupBy(itemId => itemId)
            .Select(group => new
            {
                ItemId = group.Key,
                Weight = CalculateMergePuzzleDropWeight(group.Key, group.Count(), ownedCounts)
            })
            .Where(candidate => candidate.Weight > 0)
            .ToList();

        if (weightedItems.Count == 0)
            return areaPool[_rng.Next(areaPool.Count)];

        int totalWeight = weightedItems.Sum(candidate => candidate.Weight);
        int roll = _rng.Next(totalWeight);
        int cursor = 0;

        foreach (var candidate in weightedItems)
        {
            cursor += candidate.Weight;
            if (roll < cursor)
                return candidate.ItemId;
        }

        return weightedItems[^1].ItemId;
    }

    private static int CalculateMergePuzzleDropWeight(
        int itemId,
        int basePoolCount,
        Dictionary<int, int> ownedCounts)
    {
        int weight = Math.Max(1, basePoolCount) * MergeDropBaseWeight;
        int recipeBonus = CalculateRecipeProgressBonus(itemId, ownedCounts);
        int ownedCount = ownedCounts.GetValueOrDefault(itemId);

        weight += recipeBonus;
        if (ownedCount > 0)
            weight -= ownedCount * MergeDropDuplicatePenalty;

        return Math.Max(1, weight);
    }

    private static int CalculateRecipeProgressBonus(int itemId, Dictionary<int, int> ownedCounts)
    {
        int bonus = 0;

        foreach (var recipe in BattleItemRecipeData.GetRecipesByInput(itemId))
        {
            var requiredCounts = recipe.InputItemIds
                .GroupBy(inputItemId => inputItemId)
                .ToDictionary(group => group.Key, group => group.Count());

            int missingBefore = CountMissingRecipeInputs(requiredCounts, ownedCounts);
            if (missingBefore == 0)
                continue;

            var ownedAfterDrop = new Dictionary<int, int>(ownedCounts);
            ownedAfterDrop[itemId] = ownedAfterDrop.GetValueOrDefault(itemId) + 1;
            int missingAfter = CountMissingRecipeInputs(requiredCounts, ownedAfterDrop);
            if (missingAfter >= missingBefore)
                continue;

            bool hasRecipeProgress = requiredCounts.Keys.Any(inputItemId => ownedCounts.GetValueOrDefault(inputItemId) > 0);
            bonus += MergeDropMissingIngredientBonus;

            if (hasRecipeProgress)
                bonus += MergeDropProgressBonus;
            else
                bonus += MergeDropStarterBonus;

            if (missingAfter == 0)
                bonus += MergeDropCompleteRecipeBonus;
        }

        return Math.Min(bonus, MergeDropRecipeBonusCap);
    }

    private static int CountMissingRecipeInputs(
        Dictionary<int, int> requiredCounts,
        Dictionary<int, int> ownedCounts)
    {
        int missing = 0;

        foreach (var (itemId, requiredCount) in requiredCounts)
            missing += Math.Max(0, requiredCount - ownedCounts.GetValueOrDefault(itemId));

        return missing;
    }
    private static List<int> GetAllowedRngItemPool(int areaType)
    {
        var areaPool = GameInteractableData.GetItemPoolByArea(areaType)
            .Where(IsBattleLootDropItem)
            .ToList();

        return areaPool;
    }

    private static bool IsBattleLootDropItem(int itemId)
    {
        var item = GameItemData.Get(itemId);
        return item != null
               && !BattleItemRecipeData.IsRecipeOutputItem(itemId)
               && (item.Type == ItemType.CONSUMABLE || item.Type == ItemType.MATERIAL);
    }
}

/// <summary>
///     RNG 채집 결과. 호출자가 패킷 송신/이벤트 로깅에 사용. 텍스트는 클라가 csv로 조회.
/// </summary>
public class RngCollectOutcome
{
    /// <summary>0=빈손, 1=디코이, 2=지역 아이템, 3=부품, 4=선행</summary>
    public int ResultType { get; set; }

    /// <summary>부품 ID / 지역 아이템 ID / 선행 share_group (빈손/디코이는 0)</summary>
    public int ItemId { get; set; }

    /// <summary>부품/선행 회수 시 보상 stamina</summary>
    public int StaminaReward { get; set; }

    public int CooldownSeconds { get; set; }

    /// <summary>부품 회수 성공 시 데이터 (호출자 G_TO_C_PART_COLLECTED 송신용)</summary>
    public MissionPartData? CollectedPart { get; set; }

    /// <summary>특수 연출/테스트 흐름에서 같은 상호작용으로 추가 지급된 부품.</summary>
    public List<PartCollectResult> ExtraCollectedParts { get; } = new();

    /// <summary>부품 회수와 함께 완료된 미션 그래프 노드 id.</summary>
    public List<int> CompletedMissionNodeIds { get; set; } = new();

    /// <summary>소모품 회수 시 인벤토리에 추가된 아이템 (호출자 G_TO_C_INGAME_INVENTORY_UPDATE 송신용)</summary>
    public InGameItemInfo? AddedInventoryItem { get; set; }

    /// <summary>추가 지급 부품에 대응해 인벤토리에 추가된 아이템.</summary>
    public List<InGameItemInfo> AddedExtraInventoryItems { get; } = new();

    /// <summary>예리한 관찰 보너스로 추가 지급된 소모품 ID.</summary>
    public int BonusItemId { get; set; }

    /// <summary>예리한 관찰 보너스로 인벤토리에 추가된 아이템.</summary>
    public InGameItemInfo? AddedBonusInventoryItem { get; set; }
}
