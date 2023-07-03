#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;

namespace network
{
    [MessagePackObject]
    public class C_TO_S_LOGIN
    {
        [Key("token")]
        public string account_token { get; set; } // (임시) 현재 아무 의미 없음 .. 추후 계정키로 변경 예정
    }

    [MessagePackObject]
    public class S_TO_C_LOGIN
    {
        [Key("player")]
        public PlayerObj player { get; set; }

        [Key("player_list")]
        public List<PlayerObj> player_list { get; set; }
    }

    [MessagePackObject]
    public class PlayerObj
    {
        public PlayerObj() { }

        [Key("player_id")]
        public int player_id { get; set; } // (임시)user_list 인덱스 - 추후 데이터베이스 PK로 변경 예정

        [Key("name")]
        public string name { get; set; } // 임시 이름.. 서버에서 아무렇게나
    }

    [MessagePackObject]
    public class S_TO_C_LOGIN_ALL
    {
        [Key("player")]
        public PlayerObj player;
    }

    [MessagePackObject]
    public class S_TO_C_LOGOUT_ALL
    {
        [Key("player")]
        public PlayerObj player;
    }

    [MessagePackObject]
    public class C_TO_S_CHAT_MSG
    {
        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_CHAT_MSG_ALL
    {
        [Key("player_id")]
        public int player_id { get; set; }

        [Key("chat_message")]
        public string chat_message { get; set; }
    }
}
