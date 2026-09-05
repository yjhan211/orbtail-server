using MessagePack;
using network.infrastructure.redis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

/// <summary>
///     공용 InventoryInfo 모델에 서버 전용 아이템 추가·삭제와 Redis 저장·조회 기능을 추가한다.
///     Unity에는 Common 쪽 모델만 포함되므로 Redis 의존성은 서버에만 남는다.
///
///     AddItem과 DeleteItem은 메모리의 아이템 목록과 수량만 변경한다.
///     변경 내용을 Redis에 반영하려면 Save를 별도로 호출해야 한다.
///
///     Save는 소유자 종류와 ID를 키로 인벤토리 전체를 직렬화해 저장한다.
///     Load는 저장된 데이터가 없으면 해당 소유자의 빈 인벤토리를 반환한다.
///
///     플레이어 인벤토리를 수정할 때는 호출 측에서 PlayerInfo.Lock을 획득하고,
///     조회부터 변경·저장까지 같은 잠금 범위에서 처리한다.
/// </summary>
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
