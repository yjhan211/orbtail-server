namespace game_server
{
    using MessagePack;

    [MessagePackObject]
    public partial class GameObjectInfo : IMessagePackObject
    {
        [IgnoreMember]
        const string HASH_KEY = "game_object_info";

        public GameObjectInfo()
        {
            this.object_type = ObjectType.NONE;
            this.object_id = 0;
            this.current_cell = new Cell(0, 0);
            this.target_cell = new Cell(0, 0);
            this.move_timestamp = default;
            this.is_flip = false;
        }

        [Key("object_type")]
        public ObjectType object_type { get; set; }

        [Key("object_id")]
        public long object_id { get; set; }

        [Key("map_id")]
        public int map_id { get; set; }

        [Key("current_cell")]
        public Cell current_cell { get; set; }

        [Key("target_cell")]
        public Cell target_cell { get; set; }

        [Key("move_timestamp")]
        public DateTime move_timestamp { get; set; }

        [Key("is_flip")]
        public bool is_flip { get; set; }
    }
}
