using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace game_server
{
    public enum PROTOCOL : int
    {
        HEART_BEAT = 0,
        C_TO_S_LOGIN,
        S_TO_C_LOGIN,
        S_TO_C_PLAYER_SPAWN_LIST,
        S_TO_C_PLAYER_DESTROY_LIST,
        C_TO_S_CHAT_MSG,
        S_TO_C_CHAT_MSG_ALL,
        C_TO_S_MOVE,
        S_TO_C_MOVE_LIST,
        S_TO_C_MAP_INFO,
        S_TO_C_MAP_UPDATE,
        S_TO_C_OUT_BOUND_INFO,
        S_TO_C_BOUND_TILE_INFO,
        C_TO_S_PLAYER_INFO,
        S_TO_C_PLAYER_INFO,
        C_TO_S_OBJECT_INFO,
        S_TO_C_OBJECT_INFO,
        END
    }

    public enum ObjectType : int
    {
        NONE,
        PLAYER,
        ITEM
    }

    public enum TileType : int
    {
        EMPTY,
    }

    public enum DirectionType : byte
    {
        NONE,
        TOP_LEFT,
        TOP_RIGHT,
        BOTTOM_LEFT,
        BOTTOM_RIGHT,
    }
}
