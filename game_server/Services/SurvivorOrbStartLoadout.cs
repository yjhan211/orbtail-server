using network.common.data;

namespace game_server.services;

/// <summary>
/// Grants every participant exactly one combat orb at the start of a matching.
/// The choice is stable for the matching/player pair, so reconnecting cannot add
/// a second orb or change the player's starting build.
/// </summary>
public static class SurvivorOrbStartLoadout
{
    private static readonly int[] CombatOrbT1Ids = [107000010, 107000020, 107000030];

    public static int EnsureStartingOrb(InGameInventoryManager inventoryManager, long matchingId, long playerId)
    {
        ArgumentNullException.ThrowIfNull(inventoryManager);

        var inventory = inventoryManager.GetPlayerInventory(matchingId, playerId);
        if (inventory.GetAllItems().Any(item => item.Count > 0 && SurvivorOrbData.IsSurvivorOrb(item.ItemId)))
            return 0;

        int itemId = CombatOrbT1Ids[GetStableStartIndex(matchingId, playerId)];
        inventoryManager.AddItem(matchingId, playerId, itemId);
        return itemId;
    }

    private static int GetStableStartIndex(long matchingId, long playerId)
    {
        ulong seed = unchecked((ulong)matchingId) * 11400714819323198485UL;
        seed ^= unchecked((ulong)playerId) * 14029467366897019727UL;
        seed ^= seed >> 29;
        return (int)(seed % (ulong)CombatOrbT1Ids.Length);
    }
}