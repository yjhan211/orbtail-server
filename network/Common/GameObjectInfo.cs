namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class GameObjectInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "game_object_info";

        [Key("object_type")]
        public ObjectType object_type { get; set; }

        [Key("object_id")]
        public long object_id { get; set; }

        [Key("map_id")]
        public MapID map_id { get; set; }

        [Key("current_cell")]
        public Cell current_cell { get; set; }

        [Key("target_cell")]
        public Cell target_cell { get; set; }

        [Key("move_timestamp")]
        public DateTime move_timestamp { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }

        public static string MakeHashField(ObjectType type, long object_id)
        {
            return $"{(int)type}_{object_id}";
        }

        public string GetHashField()
        {
            return $"{(int)this.object_type}_{this.object_id}";
        }

        // 이거 없애면 안됨 MessagePack에서 씀
        public GameObjectInfo()
        {
            this.object_type = ObjectType.NONE;
            this.object_id = object_id;
            this.current_cell = new Cell(0, 0);
            this.target_cell = new Cell(0, 0);
            this.move_timestamp = default;
            this.is_flip = false;
        }

        public GameObjectInfo(long object_id)
        {
            this.object_type = ObjectType.NONE;
            this.object_id = object_id;
            this.current_cell = new Cell(0, 0);
            this.target_cell = new Cell(0, 0);
            this.move_timestamp = default;
            this.is_flip = false;
        }

        public GameObjectInfo(ObjectType object_type, long object_id, Cell cell)
        {
            this.object_type = object_type;
            this.object_id = object_id;
            this.current_cell = Cell.Clone(cell);
            this.target_cell = Cell.Clone(cell);
            this.move_timestamp = DateTime.MinValue;
            this.is_flip = false;
        }

        public double GetMoveElapsedTime()
        {
            double elapsed_time = 0;
            switch (this.object_type)
            {
                case ObjectType.PLAYER:
                    elapsed_time = (DateTime.UtcNow - this.move_timestamp).TotalSeconds;
                    break;

                default:
                    break;
            }

            return elapsed_time;
        }

        public void SetFlip(DirectionType direction)
        {
            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                case DirectionType.BOTTOM_LEFT:
                    this.is_flip = true;
                    break;

                default:
                    this.is_flip = false;
                    break;
            }
        }
    }
}
