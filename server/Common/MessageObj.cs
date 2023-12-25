#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;

namespace game_server
{
    public interface IMessagePackObject { }

    [MessagePackObject]
    public class C_TO_S_LOGIN : IMessagePackObject
    {
        [Key("token")]
        public string account_token { get; set; } // (임시) 현재 아무 의미 없음 .. 추후 계정키로 변경 예정
    }

    [MessagePackObject]
    public class S_TO_C_LOGIN : IMessagePackObject
    {
        [Key("object_info")]
        public GameObjectInfo object_info { get; set; }

        [Key("player_info")]
        public PlayerInfo player_info { get; set; }
    }

    [MessagePackObject]
    public class C_TO_S_CHAT_MSG : IMessagePackObject
    {
        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_CHAT_MSG_ALL : IMessagePackObject
    {
        [Key("player_id")]
        public long player_id { get; set; }

        [Key("chat_message")]
        public string chat_message { get; set; }
    }

    [MessagePackObject]
    public class C_TO_S_MOVE : IMessagePackObject
    {
        [Key("direction")]
        public DirectionType direction { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_MAP_INFO : IMessagePackObject
    {
        [Key("object_key_list")]
        public List<string> object_key_list { get; set; }

        [Key("is_ended")]
        public bool is_ended { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_MAP_UPDATE : IMessagePackObject
    {
        [Key("object_list")]
        public List<GameObjectInfo> object_list { get; set; }
    }

    [MessagePackObject]
    public class C_TO_S_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_id_list")]
        public List<long> player_id_list { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_info_list")]
        public List<PlayerInfo> player_info_list { get; set; }
    }

    [MessagePackObject]
    public class C_TO_S_OBJECT_INFO : IMessagePackObject
    {
        [Key("object_key_list")]
        public List<string> object_key_list { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_OBJECT_INFO : IMessagePackObject
    {
        [Key("object_info_list")]
        public List<GameObjectInfo> object_info_list { get; set; }
    }

    [MessagePackObject]
    public class MapTile : IMessagePackObject
    {
        public MapTile(TileType tile_type)
        {
            this.type = tile_type;
        }

        [Key("tile_type")]
        public TileType type { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_BOUND_TILE_INFO : IMessagePackObject
    {
        [Key("tile_list")]
        public List<Cell> tile_list { get; set; }
    }
}
