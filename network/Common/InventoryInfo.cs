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

        [Key("item_dict")]
        public Dictionary<long, ItemInfo> item_dict { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public InventoryInfo()
        {
            this.owner_id = 0;
            this.item_dict = new();
        }

        public InventoryInfo(InventoryOwnerType owner_type, long owner_id)
        {
            this.owner_type = owner_type;
            this.owner_id = owner_id;
            this.item_dict = new();
        }

        public void AddItem(ItemInfo item_info)
        {
            var is_countable = !GameDesignData.IsWearableItem(item_info.item_id);
            if (is_countable)
            {
                var exist_item = this.item_dict.Values.FirstOrDefault(
                    item => item.item_id == item_info.item_id
                );

                if (exist_item != null)
                {
                    exist_item.count += item_info.count;
                    return;
                }
            }

            this.item_dict[item_info.item_uid] = item_info;
        }

        public void AddItem(List<ItemInfo> item_info_list)
        {
            foreach (var item_info in item_info_list)
            {
                var is_countable = !GameDesignData.IsWearableItem(item_info.item_id);
                if (is_countable)
                {
                    var exist_item = this.item_dict.Values.FirstOrDefault(
                        item => item.item_id == item_info.item_id
                    );

                    if (exist_item != null)
                    {
                        exist_item.count += item_info.count;
                        continue;
                    }
                }

                this.item_dict[item_info.item_uid] = item_info;
            }
        }
    }
}
