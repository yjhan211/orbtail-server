namespace game_server
{
    using network;
    using UnityEngine;

    public class Player
    {
        public readonly int player_id;
        public GameUser owner { get; private set; }
        public string name { get; private set; }
        public Vector2Int current_cell { get; private set; }
        public Vector2Int target_cell { get; private set; }
        public long move_timestamp { get; private set; }

        public Player(GameUser user, int player_id, string name)
        {
            this.owner = user;
            this.player_id = player_id;
            this.name = name;

            this.current_cell = new Vector2Int(0, 0);
            this.target_cell = new Vector2Int(0, 0);
            this.move_timestamp = 0;
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
