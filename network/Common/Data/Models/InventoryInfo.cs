// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class InventoryInfo : IMessagePackObject
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
    }
}