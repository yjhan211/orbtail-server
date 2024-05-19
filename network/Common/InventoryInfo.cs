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

        public void AddItem(ItemInfo item_info)
        {
            var is_create = true;
            var is_countable = !GameDesignData.IsWearableItem(item_info.item_id);
            if (is_countable)
            {
                for (int i = 0; i < this.item_list.Count; i++)
                {
                    if (this.item_list[i].item_id == item_info.item_id)
                    {
                        this.item_list[i].count += item_info.count;
                        is_create = false;
                        break;
                    }
                }
            }

            if (!is_create)
            {
                return;
            }

            this.item_list.Add(item_info);
        }

        public void AddItem(List<ItemInfo> item_info_list)
        {
            foreach (var item_info in item_info_list)
            {
                var is_create = true;
                var is_countable = !GameDesignData.IsWearableItem(item_info.item_id);
                if (is_countable)
                {
                    for (int i = 0; i < this.item_list.Count; i++)
                    {
                        if (this.item_list[i].item_id == item_info.item_id)
                        {
                            this.item_list[i].count += item_info.count;
                            is_create = false;
                            break;
                        }
                    }
                }

                if (!is_create)
                {
                    return;
                }

                this.item_list.Add(item_info);
            }
        }
    }
}
