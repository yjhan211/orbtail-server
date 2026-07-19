using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Keeps bot equipment decisions on the same inventory and recipe rules as players.
/// Bots choose a highest-tier available recipe, then equip their best resulting battle item.
/// </summary>
public static class BotBattleItemLoadout
{
    public static BotBattleItemLoadoutResult CombineAndEquip(
        InGameInventoryManager inventoryManager,
        long matchingId,
        long playerId,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(inventoryManager);
        ArgumentNullException.ThrowIfNull(random);

        var combinedItemIds = new List<int>();
        var inventory = inventoryManager.GetPlayerInventory(matchingId, playerId);

        // Three combines are enough for four T1 items to become one T3 in a single decision tick.
        for (int attempt = 0; attempt < 3; attempt++)
        {
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
                    matchingId,
                    playerId,
                    recipe.InputItemIds,
                    recipe.OutputItemId,
                    out _))
            {
                break;
            }

            combinedItemIds.Add(recipe.OutputItemId);
        }

        var bestItem = inventory.GetAllItems()
            .Where(item => item.Count > 0 && BattleItemCombatData.IsCombatItem(item.ItemId))
            .OrderByDescending(item => BattleItemCombatData.Get(item.ItemId)?.Tier ?? 0)
            .ThenByDescending(item => BattleItemCombatData.Get(item.ItemId)?.AttackRange ?? 0f)
            .ThenBy(item => item.ItemId)
            .FirstOrDefault();

        InGameItemInfo? equippedItem = inventory.GetEquippedBattleItem();
        if (bestItem != null && (equippedItem == null || equippedItem.ItemUid != bestItem.ItemUid))
            inventoryManager.TryEquipBattleItem(matchingId, playerId, bestItem.ItemUid, out equippedItem);

        return new BotBattleItemLoadoutResult(combinedItemIds, equippedItem?.ItemId ?? 0);
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
    int EquippedItemId);
