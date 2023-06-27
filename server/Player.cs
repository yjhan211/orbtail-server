using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace game_server
{
    using network;

    public class Player
    {
        public readonly int user_uid;
        public GameUser owner { get; private set; }
        public string name { get; private set; }

        public Player(GameUser user, int user_uid, string name)
        {
            this.owner = user;
            this.user_uid = user_uid;
            this.name = name;
        }

        public int GetUserUid()
        {
            return this.user_uid;
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
