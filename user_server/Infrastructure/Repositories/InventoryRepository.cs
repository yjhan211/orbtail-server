using network.common.data.models;
using network.interfaces;
using user_server.domain.repositories;

namespace user_server.infrastructure.repositories;

/// <summary>
/// Redis-based implementation of Inventory repository
/// </summary>
public class InventoryRepository : IInventoryRepository
{
    private readonly ICacheHelper _cacheHelper;

    public InventoryRepository(ICacheHelper cacheHelper)
    {
        _cacheHelper = cacheHelper;
    }

    public async Task<InventoryInfo?> LoadAsync(long playerId)
    {
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);
        return playerInfo?.InventoryInfo;
    }

    public async Task SaveAsync(InventoryInfo inventoryInfo)
    {
        await inventoryInfo.Save(_cacheHelper);
    }

    public async Task<ItemInfo?> GetItemAsync(long playerId, long itemUid)
    {
        var inventory = await LoadAsync(playerId);
        if (inventory?.ItemDict.TryGetValue(itemUid, out var itemInfo) == true)
        {
            return itemInfo;
        }
        return null;
    }
}
