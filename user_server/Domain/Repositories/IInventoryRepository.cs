using network.common.data.models;

namespace user_server.domain.repositories;

/// <summary>
/// Repository interface for Inventory data access
/// </summary>
public interface IInventoryRepository
{
    /// <summary>
    /// Loads inventory info for a player
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <returns>Inventory info or null if not found</returns>
    Task<InventoryInfo?> LoadAsync(long playerId);

    /// <summary>
    /// Saves inventory info to cache
    /// </summary>
    /// <param name="inventoryInfo">Inventory info to save</param>
    Task SaveAsync(InventoryInfo inventoryInfo);

    /// <summary>
    /// Gets a specific item by UID
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <param name="itemUid">Item UID</param>
    /// <returns>Item info or null if not found</returns>
    Task<ItemInfo?> GetItemAsync(long playerId, long itemUid);
}
