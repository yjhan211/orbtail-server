namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class InventoryInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "inventory_info";

        /*-----------------------------------------------------------------*/

        [Key("owner_type")]
        public InventoryOwnerType owner_type { get; set; }

        [Key("owner_id")]
        public long owner_id { get; set; }

        [Key("item_list")]
        public List<ItemInfo> item_list { get; set; } // TODO dict로 바꾸는게 나을듯

        // 이거 없애면 안됨 MessagePack에서 씀
        public InventoryInfo()
        {
            this.owner_id = 0;
            this.item_list = new();
        }

        public InventoryInfo(InventoryOwnerType owner_type, long owner_id)
        {
            this.owner_type = owner_type;
            this.owner_id = owner_id;
            this.item_list = new();
        }
    }
}
