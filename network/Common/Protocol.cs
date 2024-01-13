using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public enum PROTOCOL : int
    {
        HEART_BEAT = 0,
        C_TO_U_LOGIN,
        U_TO_C_LOGIN,
        C_TO_U_CHAT_MSG,
        U_TO_C_CHAT_MSG,
        C_TO_U_MOVE,
        U_TO_G_MOVE,
        G_TO_U_MOVE,
        G_TO_U_MAP_INFO,
        U_TO_C_MAP_INFO,
        U_TO_C_MAP_UPDATE,
        C_TO_U_PLAYER_INFO,
        U_TO_C_PLAYER_INFO,
        C_TO_U_OBJECT_INFO,
        U_TO_C_OBJECT_INFO,
        U_TO_C_BOUND_TILE_INFO,
        U_TO_G_LOGOUT,

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

    public enum LoginType : int
    {
        GUEST,
    }
}
