using MessagePack;
using network.infrastructure.redis;

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

    public async Task Save(IRedisOperations redisOperations)
    {
        await redisOperations.HashSetAsync(HashKey, $"{(int)OwnerType}_{OwnerId}",
            MessagePackSerializer.Serialize(this));
    }

    public static async Task<InventoryInfo?> Load(IRedisOperations redisOperations, InventoryOwnerType ownerType, long ownerId)
    {
        var serializedData = await redisOperations.HashGetAsync(HashKey, $"{(int)ownerType}_{ownerId}");
        if (serializedData.IsNull) return new InventoryInfo(ownerType, ownerId);

        var inventoryInfo = MessagePackSerializer.Deserialize<InventoryInfo?>(serializedData);
        return inventoryInfo;
    }

    public static async Task Delete(IRedisOperations redisOperations, InventoryOwnerType ownerType, long ownerId)
    {
        await redisOperations.HashDeleteAsync(HashKey, $"{(int)ownerType}_{ownerId}");
    }
}
