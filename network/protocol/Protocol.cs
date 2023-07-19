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
        C_TO_S_LOGIN,
        S_TO_C_LOGIN,
        S_TO_C_LOGIN_ALL,
        S_TO_C_LOGOUT_ALL,
        C_TO_S_CHAT_MSG,
        S_TO_C_CHAT_MSG_ALL,
        C_TO_S_MOVE,
        S_TO_C_MOVE_ALL,
        END
    }

    public enum TileType : int
    {
        EMPTY,
        SIDEWALK,
    }
}
