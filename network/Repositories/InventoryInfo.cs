using MessagePack;
using network.interfaces;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class InventoryInfo
{
    public ItemInfo AddItem(ItemInfo itemInfo)
    {
        var itemInfoData = GameItemData.Get(itemInfo.ItemId);
        if (!itemInfoData.Reusable)
        {
            var existItem = ItemDict.Values.FirstOrDefault(item => item.ItemId == itemInfo.ItemId);
            if (existItem != null)
            {
                existItem.Count += itemInfo.Count;
                return existItem;
            }
        }

        ItemDict[itemInfo.ItemUid] = itemInfo;
        return itemInfo;
    }

    public void AddItem(List<ItemInfo> itemInfoList)
    {
        foreach (var itemInfo in itemInfoList)
        {
            var itemInfoData = GameItemData.Get(itemInfo.ItemId);
            if (!itemInfoData.Reusable)
            {
                var existItem = ItemDict.Values.FirstOrDefault(
                    item => item.ItemId == itemInfo.ItemId
                );

                if (existItem != null)
                {
                    existItem.Count += itemInfo.Count;
                    continue;
                }
            }

            ItemDict[itemInfo.ItemUid] = itemInfo;
        }
    }

    public ItemInfo? DeleteItem(long itemUid, int count)
    {
        if (!ItemDict.TryGetValue(itemUid, out var item))
        {
            return null;
        }
        
        if (item.Count < count)
        {
            return null;
        }

        item.Count -= count;
        if (item.Count <= 0)
        {
            ItemDict.Remove(itemUid);
        }
        
        return item;
    }
    
    public ItemInfo? DeleteItemById(int itemId, int count)
    {
        var item = ItemDict.Values.FirstOrDefault(x => x.ItemId == itemId);
        if (item == null)
        {
            return null;
        }
    
        if (item.Count < count)
        {
            return null;
        }

        item.Count -= count;
        if (item.Count <= 0)
        {
            ItemDict.Remove(item.ItemUid);
        }
    
        return item;
    }

    public async Task Save(ICacheHelper cacheHelper)
    {
        await cacheHelper.HashSetAsync(HashKey, $"{(int)OwnerType}_{OwnerId}",
            MessagePackSerializer.Serialize(this));
    }

    public static async Task<InventoryInfo?> Load(ICacheHelper cacheHelper, InventoryOwnerType ownerType, long ownerId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, $"{(int)ownerType}_{ownerId}");
        if (serializedData.IsNull) return new InventoryInfo(ownerType, ownerId);

        var inventoryInfo = MessagePackSerializer.Deserialize<InventoryInfo?>(serializedData);
        return inventoryInfo;
    }

    public static async Task Delete(ICacheHelper cacheHelper, InventoryOwnerType ownerType, long ownerId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, $"{(int)ownerType}_{ownerId}");
    }
}
