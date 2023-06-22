using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace game_server
{
    public enum PROTOCOL : Int32
    {
        HEART_BEAT = 0,
        C_TO_S_LOGIN,
        S_TO_C_LOGIN,
        END
    }
}
