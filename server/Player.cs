#pragma warning disable IDE1006

namespace game_server
{
    using network;

    public class Player : GameObject
    {
        public GameUser owner { get; private set; }
        public string name { get; private set; }
        public CellPosition target_cell { get; set; }
        public DateTime move_timestamp { get; set; }
        public bool is_flip { get; set; }
        public PacketQueueProcessor packet_queue_processor { get; set; }

        public Player(GameUser user, long player_id, string name)
        {
            this.object_type = ObjectType.PLAYER;

            this.owner = user;
            this.object_id = player_id;
            this.name = name;

            var random_x = 0;
            var random_y = 0;

            this.current_cell = new CellPosition(random_x, random_y);
            this.target_cell = new CellPosition(random_x, random_y);
            this.move_timestamp = DateTime.MinValue;

            is_flip = false;

            this.packet_queue_processor = new(this);
        }

        public PlayerObj ConvertObj()
        {
            PlayerObj player_obj =
                new()
                {
                    player_id = this.object_id,
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
