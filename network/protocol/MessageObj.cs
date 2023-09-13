#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;

namespace network
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
        [Key("object_msg")]
        public GameObjectMsg object_msg { get; set; }

        [Key("player_msg")]
        public PlayerMsg player_msg { get; set; }

        [Key("name")]
        public string name { get; set; }
    }

    [MessagePackObject]
    public class GameObjectMsg : IMessagePackObject
    {
        public GameObjectMsg() { }

        [Key("object_type")]
        public ObjectType object_type { get; set; }

        [Key("object_id")]
        public long object_id { get; set; }

        [Key("current_cell")]
        public Cell current_cell { get; set; }

        [Key("target_cell")]
        public Cell target_cell { get; set; }

        [Key("move_timestamp")]
        public DateTime move_timestamp { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }
    }

    [MessagePackObject]
    public class Cell : IMessagePackObject
    {
        public Cell(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        [Key("x")]
        public int x { get; set; }

        [Key("y")]
        public int y { get; set; }

        public bool Equals(Cell target)
        {
            return this.x == target.x && this.y == target.y;
        }
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
        [Key("object_list")]
        public List<GameObjectMsg> object_list;

        [Key("delete_object_list")]
        public List<long> delete_object_list;
    }

    [MessagePackObject]
    public class C_TO_S_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_id_list")]
        public List<long> player_id { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_PLAYER_INFO : IMessagePackObject
    {
        [Key("player_msg_list")]
        public List<PlayerMsg> player_msg { get; set; }
    }

    [MessagePackObject]
    public class PlayerMsg : IMessagePackObject
    {
        [Key("player_id")]
        public long player_id { get; set; }

        [Key("name")]
        public string name { get; set; }
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
    public class BoundTile : IMessagePackObject
    {
        public BoundTile(byte x, byte y)
        {
            this.x = x;
            this.y = y;
        }

        [Key("x")]
        public byte x { get; set; }

        [Key("y")]
        public byte y { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_BOUND_TILE_INFO : IMessagePackObject
    {
        [Key("tile_list")]
        public List<BoundTile> tile_list { get; set; }
    }
}
