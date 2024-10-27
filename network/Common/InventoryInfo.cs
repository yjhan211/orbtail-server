using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.helpers;

namespace network.common;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class InventoryInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "InventoryInfo";

    public InventoryInfo()
    {
        OwnerType = InventoryOwnerType.NONE;
        OwnerId = 0;
        ItemDict = new Dictionary<long, ItemInfo>();
    }

    public InventoryInfo(InventoryOwnerType ownerType, long ownerId)
    {
        OwnerType = ownerType;
        OwnerId = ownerId;
        ItemDict = new Dictionary<long, ItemInfo>();
    }

    [Key("ownerType")] public InventoryOwnerType OwnerType { get; set; }

    [Key("ownerId")] public long OwnerId { get; set; }

    [Key("itemDict")] public Dictionary<long, ItemInfo> ItemDict { get; set; }

    public void AddItem(ItemInfo itemInfo)
    {
        var isCountable = !GameDesignData.IsWearableItem(itemInfo.ItemId);
        if (isCountable)
        {
            var existItem = ItemDict.Values.FirstOrDefault(item => item.ItemId == itemInfo.ItemId);
            if (existItem != null)
            {
                existItem.Count += itemInfo.Count;
                return;
            }
        }

        ItemDict[itemInfo.ItemUid] = itemInfo;
    }

    public void AddItem(List<ItemInfo> itemInfoList)
    {
        foreach (var itemInfo in itemInfoList)
        {
            var isCountable = !GameDesignData.IsWearableItem(itemInfo.ItemId);
            if (isCountable)
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