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
    }
}
