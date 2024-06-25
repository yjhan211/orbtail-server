#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;

namespace network
{
    public interface IMessagePackObject { }

    [MessagePackObject]
    public class U_TO_C_HEART_BEAT : IMessagePackObject
    {
        [Key("utc_now")]
        public DateTime utc_now { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_LOGIN : IMessagePackObject
    {
        [Key("token")]
        public string account_token { get; set; } // (임시) 현재 아무 의미 없음 .. 추후 계정키로 변경 예정
    }

    [MessagePackObject]
    public class U_TO_C_LOGIN : IMessagePackObject
    {
        [Key("object_info")]
        public GameObjectInfo object_info { get; set; }

        [Key("player_info")]
        public PlayerInfo player_info { get; set; }

        [Key("job_info")]
        public JobInfo job_info { get; set; }

        [Key("lab_info")]
        public LabInfo lab_info { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_INVENTORY_ITEM_LIST : IMessagePackObject
    {
        [Key("item_dict")]
        public Dictionary<long, ItemInfo> item_dict { get; set; }

        [Key("is_ended")]
        public bool is_end { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_CHAT_MSG : IMessagePackObject
    {
        [Key("chat_type")]
        public ChatType chat_type { get; set; }

        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CHAT_MSG : IMessagePackObject
    {
        [Key("chat_type")]
        public ChatType chat_type { get; set; }

        [Key("name")]
        public string name { get; set; }

        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MOVE : IMessagePackObject
    {
        [Key("direction")]
        public DirectionType direction { get; set; }
    }

    [MessagePackObject]
    public class U_TO_G_MOVE : IMessagePackObject
    {
        [Key("object_info")]
        public GameObjectInfo object_info { get; set; }

        [Key("target_cell")]
        public Cell target_cell { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_MOVE : IMessagePackObject
    {
        [Key("object_info")]
        public GameObjectInfo object_info { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MOVE : IMessagePackObject
    {
        [Key("error_code")]
        public ErrorCode error_code { get; set; }

        [Key("object_info")]
        public GameObjectInfo object_info { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_SPAWN : IMessagePackObject
    {
        [Key("object_key_list")]
        public List<string> object_key_list { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_DESTROY : IMessagePackObject
    {
        [Key("object_key")]
        public string object_key { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_SPAWN : IMessagePackObject
    {
        [Key("object_key_list")]
        public List<string> object_key_list { get; set; }

        [Key("is_ended")]
        public bool is_ended { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_DESTROY : IMessagePackObject
    {
        [Key("object_key")]
        public string object_key { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAP_UPDATE : IMessagePackObject
    {
        [Key("object_list")]
        public List<GameObjectInfo> object_list { get; set; }

        [Key("server_timestamp")]
        public DateTime server_timestamp { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_id_list")]
        public List<long> player_id_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_info_list")]
        public List<PlayerInfo> player_info_list { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_info")]
        public PlayerInfo player_info { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_CAMP_INFO : IMessagePackObject
    {
        [Key("camp_info")]
        public CampInfo camp_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_OBJECT_INFO : IMessagePackObject
    {
        [Key("object_key_list")]
        public List<string> object_key_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_OBJECT_INFO : IMessagePackObject
    {
        [Key("object_info_list")]
        public List<GameObjectInfo> object_info_list { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("explore_target_id_list")]
        public List<long> explore_target_id_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("explore_target_info")]
        public List<ExploreTargetInfo> explore_target_info_list { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_EXPLORE_TARGET_INFO : IMessagePackObject
    {
        [Key("explore_target_info")]
        public ExploreTargetInfo explore_target_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_JOB_RESOURCE_INFO : IMessagePackObject
    {
        [Key("job_resource_id_list")]
        public List<long> job_resource_id_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_JOB_RESOURCE_INFO : IMessagePackObject
    {
        [Key("job_resource_info")]
        public List<JobResourceInfo> job_resource_info_list { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_JOB_RESOURCE_INFO : IMessagePackObject
    {
        [Key("job_resource_info")]
        public JobResourceInfo job_resource_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_USE_SKILL : IMessagePackObject
    {
        [Key("target_job_resource_uid")]
        public long resource_uid { get; set; }

        [Key("skill_id")]
        public int skill_id { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_USE_SKILL : IMessagePackObject
    {
        [Key("error_code")]
        public ErrorCode error_code { get; set; }

        [Key("job_info")]
        public JobInfo? job_info { get; set; }
    }

    [MessagePackObject]
    public class U_TO_G_USE_SKILL : IMessagePackObject
    {
        [Key("resource_uid")]
        public long resource_uid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_USE_SKILL_COMPLETE : IMessagePackObject
    {
        [Key("is_success")]
        public bool is_success { get; set; }

        [Key("item_info")]
        public ItemInfo item_info { get; set; }

        [Key("job_info")]
        public JobInfo job_info { get; set; }
    }

    [MessagePackObject]
    public class U_TO_G_LOGOUT : IMessagePackObject
    {
        [Key("player_id")]
        public long player_id { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_GET_JOB : IMessagePackObject
    {
        [Key("job_type")]
        public JobType job_type { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_GET_JOB : IMessagePackObject
    {
        [Key("error_code")]
        public ErrorCode error_code { get; set; }

        [Key("job_info")]
        public JobInfo job_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_UPGRADE_JOB : IMessagePackObject
    {
        [Key("job_type")]
        public JobType job_type { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_UPGRADE_JOB : IMessagePackObject
    {
        [Key("error_code")]
        public ErrorCode error_code { get; set; }

        [Key("job_info")]
        public JobInfo job_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_WEAR_ITEM : IMessagePackObject
    {
        [Key("item_uid")]
        public long item_uid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_WEAR_ITEM : IMessagePackObject
    {
        [Key("player_info")]
        public PlayerInfo player_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_USE_ITEM : IMessagePackObject
    {
        [Key("item_uid")]
        public long item_uid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_USE_ITEM : IMessagePackObject
    {
        [Key("job_info")]
        public JobInfo job_info { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CHANGE_MAP : IMessagePackObject
    {
        [Key("map_id")]
        public MapID map_id { get; set; }

        [Key("map_sub_id")]
        public long map_sub_id { get; set; }

        [Key("spawn_cell")]
        public Cell spawn_cell { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_CREATE_LAB : IMessagePackObject
    {
        [Key("lab_name")]
        public string lab_name { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CREATE_LAB : IMessagePackObject
    {
        [Key("player_info")]
        public PlayerInfo player_info { get; set; }

        [Key("lab_info")]
        public LabInfo lab_info { get; set; }
    }

    [MessagePackObject]
    public class G_TO_U_CREATE_INSTANCE_SUCCESS : IMessagePackObject
    {
        [Key("map_id")]
        public MapID map_id { get; set; }

        [Key("map_sub_id")]
        public long map_sub_id { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_UPGRADE_RESEARCH : IMessagePackObject
    {
        [Key("research_id")]
        public int research_id { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_UPGRADE_RESEARCH : IMessagePackObject
    {
        [Key("reserach_info_dict")]
        public Dictionary<int, ResearchInfo> reserach_info_dict { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MAKE : IMessagePackObject
    {
        [Key("materials")]
        public Dictionary<long, int> materials { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAKE : IMessagePackObject
    {
        [Key("is_success")]
        public bool is_success { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_WRITE_LAB_HIRE : IMessagePackObject
    {
        [Key("comment")]
        public string comment { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_WRITE_LAB_HIRE : IMessagePackObject
    {
        [Key("error_code")]
        public ErrorCode error_code { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_LAB_HIRE_LIST : IMessagePackObject
    {
        [Key("hire_list")]
        public List<(long, string, string)> hire_list { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_JOIN_LAB : IMessagePackObject
    {
        [Key("lab_id")]
        public long lab_id { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_LAB_INFO : IMessagePackObject
    {
        [Key("join_player_info")]
        public PlayerInfo join_player_info { get; set; }

        [Key("lab_info")]
        public LabInfo lab_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_LAB_INVENTORY_ADD_ITEM : IMessagePackObject
    {
        [Key("item_uid")]
        public long item_uid { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_LAB_INVENTORY_TAKE_ITEM : IMessagePackObject
    {
        [Key("item_uid")]
        public long item_uid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_U_LAB_INVENTORY : IMessagePackObject
    {
        [Key("item_dict")]
        public Dictionary<long, ItemInfo> item_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_LAB_INVENTORY : IMessagePackObject
    {
        [Key("item_dict")]
        public Dictionary<long, ItemInfo> item_dict { get; set; }

        [Key("is_ended")]
        public bool is_end { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_ENCAMP : IMessagePackObject
    {
        [Key("item_uid")]
        public long item_uid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_CAMP_INFO : IMessagePackObject
    {
        [Key("camp_info_list")]
        public List<CampInfo> camp_info_list { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_CAMP_INFO : IMessagePackObject
    {
        [Key("camp_id_list")]
        public List<long> camp_id_list { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_UPDATE_HP : IMessagePackObject
    {
        [Key("add_hp")]
        public int add_hp { get; set; }

        [Key("current_hp")]
        public int current_hp { get; set; }
    }
}
