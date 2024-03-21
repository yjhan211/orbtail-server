namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class InventoryInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "inventory_info";

        [Key("player_id")]
        public long player_id { get; set; }

        [Key("item_list")]
        public List<ItemInfo> item_list { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public InventoryInfo()
        {
            this.player_id = 0;
            this.item_list = new();
        }

        public InventoryInfo(long player_id)
        {
            this.player_id = player_id;
            this.item_list = new();
        }
    }
}
