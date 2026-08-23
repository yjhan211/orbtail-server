using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Keeps bot equipment decisions on the same inventory and random Survivor-orb rules as players.
/// After every evolution bots prefer an equipped orb that still has a same-colour support orb.
/// </summary>
public static class BotBattleItemLoadout
{
    public static BotOrbDestroyDecision? SelectOverflowDestroyCandidate(
        PlayerInGameInventory inventory,
        int corruption,
        int maxCorruption)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var items = inventory.GetAllItems()
            .Where(item => item.Count > 0)
            .ToList();
        if (items.Count == 0 || HasValidMerge(items))
            return null;

        var boardItemIds = items
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .ToList();
        bool hasDominantColor = OrbData.TryGetDominantPveColor(
            boardItemIds,
            out OrbColor dominantColor);
        float corruptionRatio = maxCorruption > 0
            ? Math.Clamp((float)corruption / maxCorruption, 0f, 1f)
            : 0f;

        return items
            .Select(item => CreateDestroyDecision(
                item,
                boardItemIds,
                hasDominantColor ? dominantColor : OrbColor.None,
                corruptionRatio))
            .Where(decision => decision != null)
            .Select(decision => decision!)
            .OrderBy(decision => decision.KeepScore)
            .ThenBy(decision => decision.Tier)
            .ThenBy(decision => decision.ItemUid)
            .FirstOrDefault();
    }

    public static BotBattleItemLoadoutResult CombineAndEquip(
        InGameInventoryManager inventoryManager,
        long matchingId,
        long playerId,
        Random random,
        bool allowSurvivorOrbMerges = true)
    {
        ArgumentNullException.ThrowIfNull(inventoryManager);
        ArgumentNullException.ThrowIfNull(random);

        var combinedItemIds = new List<int>();
        var survivorOrbMerges = new List<BotSurvivorOrbMerge>();
        var inventory = inventoryManager.GetPlayerInventory(matchingId, playerId);

        // At most three merges can turn four T1 orbs into one T3 orb.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var survivorInputs = inventory.GetAllItems()
                .Where(item => item.Count > 0)
                .Select(item => item.ItemId)
                .Where(IsOrb)
                .GroupBy(itemId => itemId)
                .Where(group => group.Count() >= 2 && OrbData.CanMerge(group.Key, group.Key))
                .OrderBy(group => ConsumesLastEquippedResonanceSupport(group.Key, inventory) ? 1 : 0)
                .ThenByDescending(group =>
                {
                    OrbData.TryGetColorAndTier(group.Key, out _, out int tier);
                    return tier;
                })
                .Select(group => group.Key)
                .ToList();

            if (survivorInputs.Count > 0)
            {
                // During the resonance hold, do not let a legacy recipe consume the same orb pair.
                if (!allowSurvivorOrbMerges)
                    break;

                int inputItemId = survivorInputs[random.Next(survivorInputs.Count)];
                if (!inventoryManager.TryCombineSurvivorOrbs(
                        matchingId, playerId, inputItemId, inputItemId, random,
                        out int outputItemId, out _))
                {
                    break;
                }

                combinedItemIds.Add(outputItemId);
                survivorOrbMerges.Add(new BotSurvivorOrbMerge(inputItemId, outputItemId));
                continue;
            }

            // Legacy non-Survivor battle recipes remain available outside the P1 orb board.
            var itemIds = inventory.GetAllItems()
                .Where(item => item.Count > 0)
                .Select(item => item.ItemId)
                .ToList();
            var candidates = BattleItemRecipeData.GetAllRecipes()
                .Where(recipe => BattleItemCombatData.IsCombatItem(recipe.OutputItemId))
                .Where(recipe => HasInputs(itemIds, recipe.InputItemIds))
                .ToList();
            if (candidates.Count == 0)
                break;

            int highestTier = candidates
                .Select(recipe => BattleItemCombatData.Get(recipe.OutputItemId)?.Tier ?? 0)
                .Max();
            var bestCandidates = candidates
                .Where(recipe => (BattleItemCombatData.Get(recipe.OutputItemId)?.Tier ?? 0) == highestTier)
                .ToList();
            var recipe = bestCandidates[random.Next(bestCandidates.Count)];
            if (!inventoryManager.TryCombineItems(
                    matchingId, playerId, recipe.InputItemIds, recipe.OutputItemId, out _))
            {
                break;
            }

            combinedItemIds.Add(recipe.OutputItemId);
        }

        var allItems = inventory.GetAllItems().Where(item => item.Count > 0).ToList();
        var bestItem = allItems
            .Where(item => BattleItemCombatData.IsCombatItem(item.ItemId))
            .OrderByDescending(item => HasSameColorSupport(item, allItems))
            .ThenByDescending(item => BattleItemCombatData.Get(item.ItemId)?.Tier ?? 0)
            .ThenByDescending(item => BattleItemCombatData.Get(item.ItemId)?.AttackRange ?? 0f)
            .ThenBy(item => item.ItemId)
            .FirstOrDefault();

        InGameItemInfo? equippedItem = inventory.GetEquippedBattleItem();
        if (bestItem != null && (equippedItem == null || equippedItem.ItemUid != bestItem.ItemUid))
            inventoryManager.TryEquipBattleItem(matchingId, playerId, bestItem.ItemUid, out equippedItem);

        return new BotBattleItemLoadoutResult(combinedItemIds, equippedItem?.ItemId ?? 0, survivorOrbMerges);
    }

    private static BotOrbDestroyDecision? CreateDestroyDecision(
        InGameItemInfo item,
        IReadOnlyCollection<int> boardItemIds,
        OrbColor dominantColor,
        float corruptionRatio)
    {
        OrbColor color;
        int tier;
        if (!OrbData.TryGetColorAndTier(item.ItemId, out color, out tier))
        {
            if (!OrbData.TryGetRecoveryTier(item.ItemId, out tier))
                return null;
            color = OrbColor.Recovery;
        }

        int keepScore = tier * 100;
        keepScore += tier switch
        {
            1 => 30,
            2 => 15,
            _ => 0
        };

        if (color == OrbColor.Recovery)
        {
            keepScore += (int)Math.Round(corruptionRatio * 300f);
            if (corruptionRatio < 0.2f)
                keepScore -= 40;
        }
        else
        {
            keepScore += 20;
            keepScore += boardItemIds.Count(itemId =>
            {
                return OrbData.TryGetColorAndTier(itemId, out OrbColor otherColor, out _) &&
                       otherColor == color;
            }) * 12;

            if (color == dominantColor)
                keepScore += 120;
        }

        return new BotOrbDestroyDecision(item.ItemUid, item.ItemId, tier, color, keepScore);
    }

    /// <summary>
    /// A third copy of the same orb means merging two of them still leaves one behind,
    /// so the colour keeps its resonance pair once the merged result lands.
    /// Waiting for a full board never fires in practice: bots hold five or six orbs
    /// (2026-07-30, five matches — bots merged 0 times while players merged 39).
    /// </summary>
    public static bool HasResonanceSafeMerge(IReadOnlyCollection<InGameItemInfo> items)
    {
        return items
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .GroupBy(itemId => itemId)
            .Any(group => group.Count() >= 3 && OrbData.CanMerge(group.Key, group.Key));
    }

    private static bool HasValidMerge(IReadOnlyCollection<InGameItemInfo> items)
    {
        var itemIds = items
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .ToList();
        if (itemIds
            .GroupBy(itemId => itemId)
            .Any(group => group.Count() >= 2 && OrbData.CanMerge(group.Key, group.Key)))
        {
            return true;
        }

        return BattleItemRecipeData.GetAllRecipes()
            .Where(recipe => BattleItemCombatData.IsCombatItem(recipe.OutputItemId))
            .Any(recipe => HasInputs(itemIds, recipe.InputItemIds));
    }

    private static bool IsOrb(int itemId) =>
        OrbData.IsSurvivorOrb(itemId) || OrbData.IsRecoveryOrb(itemId);

    private static bool ConsumesLastEquippedResonanceSupport(int inputItemId, PlayerInGameInventory inventory)
    {
        var equipped = inventory.GetEquippedBattleItem();
        if (equipped == null ||
            !OrbData.TryGetColorAndTier(equipped.ItemId, out OrbColor equippedColor, out _) ||
            !OrbData.TryGetColorAndTier(inputItemId, out OrbColor inputColor, out _))
            return false;

        if (inputColor != equippedColor)
            return false;

        return inventory.GetAllItems().Count(item => item.Count > 0 &&
            OrbData.TryGetColorAndTier(item.ItemId, out OrbColor color, out _) &&
            color == equippedColor) <= 2;
    }

    private static bool HasSameColorSupport(InGameItemInfo item, IReadOnlyCollection<InGameItemInfo> allItems)
    {
        if (!OrbData.TryGetColorAndTier(item.ItemId, out OrbColor color, out _))
            return false;

        return allItems.Any(other => other.ItemUid != item.ItemUid && other.Count > 0 &&
                                     OrbData.TryGetColorAndTier(other.ItemId, out OrbColor otherColor,
                                         out _) && otherColor == color);
    }

    private static bool HasInputs(IReadOnlyCollection<int> itemIds, IReadOnlyCollection<int> requiredItemIds)
    {
        var available = itemIds.GroupBy(itemId => itemId)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var required in requiredItemIds)
        {
            if (!available.TryGetValue(required, out int count) || count == 0)
                return false;
            available[required] = count - 1;
        }

        return true;
    }
}

public sealed record BotBattleItemLoadoutResult(
    IReadOnlyList<int> CombinedItemIds,
    int EquippedItemId,
    IReadOnlyList<BotSurvivorOrbMerge> SurvivorOrbMerges);

public sealed record BotSurvivorOrbMerge(int InputItemId, int OutputItemId);

public sealed record BotOrbDestroyDecision(
    long ItemUid,
    int ItemId,
    int Tier,
    OrbColor Color,
    int KeepScore);
