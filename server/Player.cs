using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace game_server
{
    using network;

    public class Player
    {
        public readonly int player_id;
        public GameUser owner { get; private set; }
        public string name { get; private set; }

        public Player(GameUser user, int player_id, string name)
        {
            this.owner = user;
            this.player_id = player_id;
            this.name = name;
        }

        public PlayerObj ConvertObj()
        {
            PlayerObj player_obj = new() { player_id = this.player_id, name = this.name };
            return player_obj;
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
