using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace game_server
{
    using network;

    public abstract class GameObject
    {
        public ObjectType object_type { get; set; }
        public long object_id { get; set; }
        public CellPosition current_cell { get; set; }
        public CellPosition target_cell { get; set; }
        public DateTime move_timestamp { get; set; }
        public bool is_flip { get; set; }

        public GameObjectMsg ParseToMsg()
        {
            GameObjectMsg game_object_msg =
                new()
                {
                    object_type = this.object_type,
                    object_id = this.object_id,
                    current_cell = this.current_cell,
                    target_cell = this.target_cell,
                    move_timestamp = this.move_timestamp,
                    is_flip = this.is_flip
                };

            return game_object_msg;
        }
    }
}
