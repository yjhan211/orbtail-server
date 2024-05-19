namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class PlayerInfo : IMessagePackObject
    {
        // 원래는 PlayerInfo가 GameObjectInfo를 상속받는 형식을 의도했으나
        // MessagePackObject 역직렬화 과정에 문제가 있어서 이렇게 됨
        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [IgnoreMember]
        public JobInfo job_info { get; set; }

        [IgnoreMember]
        public InventoryInfo inventory_info { get; set; }

        /*-----------------------------------------------------------------*/

        [IgnoreMember]
        public const string HASH_KEY = "player_info";

        [Key("player_id")]
        public long player_id { get; set; }

        [Key("name")]
        public string name { get; set; }

        [Key("grade")]
        public PlayerGrade grade { get; set; }

        [Key("wear_item_id_list")]
        public List<int> wear_items { get; set; }

        [Key("state")]
        public PlayerState state { get; set; }

        [Key("lab_id")]
        public long lab_id { get; set; }

        [Key("lab_name")]
        public string lab_name { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public PlayerInfo()
        {
            this.player_id = 0;
            this.name = "";
            this.grade = PlayerGrade.NONE;
            this.wear_items = new List<int>();
            this.state = PlayerState.NONE;

            this.object_info = new GameObjectInfo();
            this.job_info = new JobInfo();
            this.inventory_info = new InventoryInfo();

            this.lab_id = 0;
            this.lab_name = "";
        }

        public PlayerInfo(long player_id, string name, Cell cell)
        {
            this.player_id = player_id;
            this.name = name;
            this.grade = PlayerGrade.COMMONER;
            this.wear_items = new List<int>();
            this.state = PlayerState.NONE;

            this.object_info = new GameObjectInfo(
                ObjectType.PLAYER,
                player_id,
                MapID.CITY_1,
                0,
                cell
            );

            this.job_info = new JobInfo(player_id);
            this.inventory_info = new InventoryInfo(InventoryOwnerType.PLAYER, player_id);

            this.lab_id = 0;
            this.lab_name = "";
        }

        public string GetLockKey()
        {
            return $"player_lock_{this.player_id}";
        }

        public static string GetLockKey(long player_id)
        {
            return $"player_lock_{player_id}";
        }

        /*-----------------------------------------------------------------*/

        public void WearItem(long item_uid)
        {
            var target_item_index = this.inventory_info.item_list.FindIndex(
                item => item.item_uid == item_uid
            );

            var target_item = this.inventory_info.item_list[target_item_index];
            if (target_item == null)
            {
                throw new Exception($"Item with uid {item_uid} not found");
            }

            if (!GameDesignData.IsWearableItem(target_item.item_id))
            {
                throw new Exception($"not wearable item {target_item.item_id}");
            }

            if (
                !GameDesignData.IsWearableJobInfo(
                    target_item.item_id,
                    job_info.job_type,
                    job_info.job_grade
                )
            )
            {
                throw new Exception($"not wearable job type. {target_item.item_id}");
            }

            if (target_item.is_wear)
            {
                // 착용 해제
                this.wear_items.Remove(target_item.item_id);
                target_item.is_wear = false;
            }
            else
            {
                // 같은 종류의 아이템 인덱스 찾기
                var last_wear_item_index = this.inventory_info.item_list.FindIndex(
                    item =>
                        GameDesignData.IsSameTypeItem(item.item_id, target_item.item_id)
                        && item.is_wear
                );

                if (last_wear_item_index != -1)
                {
                    // 같은 종류 아이템 착용 해제
                    this.inventory_info.item_list[last_wear_item_index].is_wear = false;
                    this.wear_items.Remove(
                        this.inventory_info.item_list[last_wear_item_index].item_id
                    );
                }

                // 새로운 아이템 착용
                this.inventory_info.item_list[target_item_index].is_wear = true;
                this.wear_items.Add(target_item.item_id);
            }
        }

        public void UseItem(long item_uid)
        {
            var target_item_index = this.inventory_info.item_list.FindIndex(
                item => item.item_uid == item_uid
            );

            var target_item = this.inventory_info.item_list[target_item_index];
            if (target_item == null)
            {
                throw new Exception($"Item with uid {item_uid} not found");
            }
            if (target_item.count <= 0)
            {
                throw new Exception($"Item {item_uid} count invalid");
            }

            if (!GameDesignData.IsUseableItem(target_item.item_id))
            {
                throw new Exception($"not useable item {target_item.item_id}");
            }

            switch (target_item.item_id)
            {
                case 201000001:
                    this.job_info.hp = Math.Min(
                        GameDesignData.GetMaxHP(this.job_info.job_grade),
                        this.job_info.hp + 20
                    );
                    break;
            }

            if (target_item.count <= 1)
            {
                this.inventory_info.item_list.RemoveAt(target_item_index);
            }
            else
            {
                // TODO 일괄사용
                this.inventory_info.item_list[target_item_index].count -= 1;
            }
        }
    }
}
