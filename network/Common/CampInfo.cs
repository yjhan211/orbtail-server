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

        [Key("add_hp_timestamp")]
        public DateTime add_hp_timestamp { get; set; } // 피 채워지는 시간

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

        public async Task Save()
        {
            await this.object_info.Save();
            await CacheHelper.Instance.HashSetAsync(
                CampInfo.HASH_KEY,
                this.player_id,
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<CampInfo?> Load(long player_id)
        {
            var serialized_data = await CacheHelper.Instance.HashGetAsync(
                CampInfo.HASH_KEY,
                player_id
            );

            if (serialized_data.IsNull)
            {
                return null;
            }

            var camp_info = MessagePackSerializer.Deserialize<CampInfo?>(serialized_data);

            if (camp_info == null)
            {
                return null;
            }

            var object_info = await GameObjectInfo.Load(ObjectType.CAMP, player_id);
            if (object_info == null)
            {
                return null;
            }

            camp_info.object_info = object_info;

            return camp_info;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(CampInfo.HASH_KEY, this.player_id);
        }

        public static async Task Delete(long player_id)
        {
            await CacheHelper.Instance.HashDeleteAsync(CampInfo.HASH_KEY, player_id);
        }
    }
}
