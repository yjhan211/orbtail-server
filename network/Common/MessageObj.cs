#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;

namespace network
{
    public interface IMessagePackObject { }

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
}
