using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace game_server
{
    public enum PROTOCOL : Int32
    {
        BEGIN = 0,

        CHAT_MSG_REQ = 1,
        CHAT_MSG_ACK = 2,

        END
    }
}
