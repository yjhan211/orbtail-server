#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;
using UnityEngine;

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
        public long player_id { get; set; } // (임시)user_list 인덱스 - 추후 데이터베이스 PK로 변경 예정

        [Key("name")]
        public string name { get; set; } // 임시 이름.. 서버에서 아무렇게나

        [Key("current_cell")]
        public Vector2Int current_cell { get; set; }

        [Key("target_cell")]
        public Vector2Int target_cell { get; set; }

        [Key("move_timestamp")]
        public long move_timestamp { get; set; }
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
        public long player_id { get; set; }

        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class C_TO_S_MOVE
    {
        [Key("direction")]
        public Vector2 direction { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_MOVE_ALL
    {
        [Key("player_id")]
        public long player_id { get; set; }

        [Key("current_cell")]
        public Vector2Int current_cell { get; set; }

        [Key("target_cell")]
        public Vector2Int target_cell { get; set; }

        [Key("move_timestamp")]
        public long move_timestamp { get; set; }
    }

    [MessagePackObject]
    public class MapTile
    {
        public MapTile(TileType tile_type)
        {
            this.type = tile_type;
        }

        [Key("tile_type")]
        public TileType type { get; set; }
    }
}
