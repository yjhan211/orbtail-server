using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace game_server
{
    using network;

    public class Player
    {
        public GameUser owner;

        public Player(GameUser user)
        {
            owner = user;
        }

        public void Send(Packet msg, bool is_broadcast)
        {
            this.owner.Send(msg);
            if (!is_broadcast)
            {
                Packet.Destroy(msg);
            }
        }
    }
}
