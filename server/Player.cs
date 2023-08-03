#pragma warning disable IDE1006

namespace game_server
{
    using network;

    public class Player
    {
        public readonly int player_id;
        public GameUser owner { get; private set; }
        public string name { get; private set; }
        public CellPosition current_cell { get; set; }
        public CellPosition target_cell { get; set; }
        public DateTime move_timestamp { get; set; }
        public bool is_flip { get; set; }

        public Player(GameUser user, int player_id, string name)
        {
            this.owner = user;
            this.player_id = player_id;
            this.name = name;

            this.current_cell = new CellPosition(0, 0);
            this.target_cell = new CellPosition(0, 0);
            this.move_timestamp = DateTime.MinValue;

            is_flip = false;
        }

        public PlayerObj ConvertObj()
        {
            PlayerObj player_obj =
                new()
                {
                    player_id = this.player_id,
                    name = this.name,
                    current_cell = this.current_cell,
                    target_cell = this.target_cell,
                    move_timestamp = this.move_timestamp,
                    is_flip = this.is_flip,
                };

            return player_obj;
        }

        public void Send(Packet msg)
        {
            this.owner.Send(msg);
            Packet.Destroy(msg);
        }
    }
}
