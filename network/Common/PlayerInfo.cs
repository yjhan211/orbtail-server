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

            this.object_info = new GameObjectInfo(ObjectType.PLAYER, player_id, cell);
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
    }
}
