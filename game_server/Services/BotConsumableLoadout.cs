using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Applies only the explicit consumable-merge recipes to bot inventory. Combat-item awakening
/// remains owned by <see cref="BotBattleItemLoadout"/>, so the two decisions cannot consume each
/// other's materials.
/// </summary>
public static class BotConsumableLoadout
{
    private const string ConsumableMergeCategory = "consumable_merge";
    private const int MaxMergesPerTick = 8;

    public static IReadOnlyList<int> MergeAvailable(
        InGameInventoryManager inventoryManager,
        long matchingId,
        long playerId)
    {
        ArgumentNullException.ThrowIfNull(inventoryManager);

        var mergedItemIds = new List<int>();
        var inventory = inventoryManager.GetPlayerInventory(matchingId, playerId);

        for (int attempt = 0; attempt < MaxMergesPerTick; attempt++)
        {
            var itemIds = inventory.GetAllItems()
                .Where(item => item.Count > 0)
                .Select(item => item.ItemId)
                .ToList();
            var recipe = BattleItemRecipeData.GetAllRecipes()
                .Where(candidate => string.Equals(
                    candidate.Category,
                    ConsumableMergeCategory,
                    StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(candidate => HasInputs(itemIds, candidate.InputItemIds));
            if (recipe == null)
                break;

            if (!inventoryManager.TryCombineItems(
                    matchingId,
                    playerId,
                    recipe.InputItemIds,
                    recipe.OutputItemId,
                    out _))
            {
                break;
            }

            mergedItemIds.Add(recipe.OutputItemId);
        }

        return mergedItemIds;
    }

    private static bool HasInputs(IReadOnlyCollection<int> itemIds, IReadOnlyCollection<int> requiredItemIds)
    {
        var available = itemIds
            .GroupBy(itemId => itemId)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (int requiredItemId in requiredItemIds)
        {
            if (!available.TryGetValue(requiredItemId, out int count) || count <= 0)
                return false;
            available[requiredItemId] = count - 1;
        }

        return true;
    }
}
