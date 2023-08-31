#pragma warning disable CS8618
#pragma warning disable IDE1006

using MessagePack;
using System.Collections.Generic;

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
        [Key("player")]
        public PlayerObj player { get; set; }
    }

    [MessagePackObject]
    public class PlayerObj : IMessagePackObject
    {
        public PlayerObj() { }

        [Key("player_id")]
        public long player_id { get; set; } // (임시)user_list 인덱스 - 추후 데이터베이스 PK로 변경 예정

        [Key("name")]
        public string name { get; set; } // 임시 이름.. 서버에서 아무렇게나

        [Key("current_cell")]
        public CellPosition current_cell { get; set; }

        [Key("target_cell")]
        public CellPosition target_cell { get; set; }

        [Key("move_timestamp")]
        public DateTime move_timestamp { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }
    }

    [MessagePackObject]
    public class CellPosition : IMessagePackObject
    {
        public CellPosition(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        [Key("x")]
        public int x { get; set; }

        [Key("y")]
        public int y { get; set; }

        public bool Equals(CellPosition target)
        {
            return this.x == target.x && this.y == target.y;
        }
    }

    [MessagePackObject]
    public class S_TO_C_PLAYER_SPAWN_LIST : IMessagePackObject
    {
        [Key("player_list")]
        public List<PlayerObj> player_list;
    }

    [MessagePackObject]
    public class S_TO_C_PLAYER_DESTROY_LIST : IMessagePackObject
    {
        [Key("player_id_list")]
        public List<long> player_id_list;
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
    public class MoveObj : IMessagePackObject
    {
        [Key("player_id")]
        public long player_id { get; set; }

        [Key("current_cell")]
        public CellPosition current_cell { get; set; }

        [Key("target_cell")]
        public CellPosition target_cell { get; set; }

        [Key("move_timestamp")]
        public DateTime move_timestamp { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }
    }

    [MessagePackObject]
    public abstract class MapObject
    {
        [Key("object_type")]
        public ObjectType object_type { get; set; }

        [Key("object_id")]
        public long object_id { get; set; }

        [Key("current_cell")]
        public CellPosition current_cell { get; set; }
    }

    [MessagePackObject]
    public class S_TO_C_MAP_INFO : IMessagePackObject
    {
        [Key("map_object_list")]
        public List<MapObject> map_object_list;
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
