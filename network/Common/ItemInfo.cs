namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class ItemInfo : IMessagePackObject
    {
        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [Key("item_id")]
        public long item_id { get; set; } // 유니크 아이디x 식별자

        [Key("count")]
        public int count { get; set; } // 수량

        [Key("is_wear")]
        public bool is_wear { get; set; } // 착용 여부

        // 이거 없애면 안됨 MessagePack에서 씀
        public ItemInfo()
        {
            this.item_id = 0;
            this.count = 0;
            this.object_info = new();
        }

        public ItemInfo(long item_id, int count, long player_id = 0)
        {
            this.item_id = item_id;
            this.count = count;
            this.is_wear = false;
            this.object_info = new(player_id);
        }
    }
}
