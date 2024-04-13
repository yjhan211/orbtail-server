namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class CampInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "camp_info";

        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        /*-----------------------------------------------------------------*/

        [Key("player_id")]
        public long player_id { get; set; }

        [Key("player_name")]
        public string player_name { get; set; }

        [Key("item_info")]
        public ItemInfo item_info { get; set; }

        public CampInfo()
        {
            this.object_info = new();
            this.item_info = new();
            this.player_id = new();
            this.player_name = "";
        }

        public CampInfo(
            long player_id,
            string player_name,
            GameObjectInfo player_object_info,
            ItemInfo item_info,
            Cell cell
        )
        {
            this.player_id = player_id;
            this.player_name = player_name;
            this.item_info = item_info;
            this.object_info = new(
                ObjectType.CAMP,
                this.player_id,
                player_object_info.map_id,
                player_object_info.map_sub_id,
                Cell.Clone(cell),
                player_object_info.is_flip
            );
        }
    }
}
