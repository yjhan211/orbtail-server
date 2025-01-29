using MessagePack;
using network.helpers;

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

    public bool DeleteItem(long itemUid, int count)
    {
        if (!ItemDict.TryGetValue(itemUid, out var item))
        {
            return false;
        }
        
        if (item.Count < count)
        {
            return false;
        }

        item.Count -= count;
        if (item.Count <= 0)
        {
            ItemDict.Remove(itemUid);
        }
        
        return true;
    }

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, $"{(int)OwnerType}_{OwnerId}",
            MessagePackSerializer.Serialize(this));
    }

    public static async Task<InventoryInfo?> Load(InventoryOwnerType ownerType, long ownerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, $"{(int)ownerType}_{ownerId}");
        if (serializedData.IsNull) return new InventoryInfo(ownerType, ownerId);

        var inventoryInfo = MessagePackSerializer.Deserialize<InventoryInfo?>(serializedData);
        return inventoryInfo;
    }

    public static async Task Delete(InventoryOwnerType ownerType, long ownerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, $"{(int)ownerType}_{ownerId}");
    }
}