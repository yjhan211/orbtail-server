namespace game_server
{
    using MessagePack;
    using StackExchange.Redis;
    using System.Text.Json;

    public partial class GameObjectInfo
    {
        static string GetHashField(ObjectType type, long object_id)
        {
            return $"{(int)type}:{object_id}";
        }

        public GameObjectInfo(ObjectType object_type, long player_id, Cell cell)
        {
            this.object_type = object_type;
            this.object_id = player_id;
            this.current_cell = Cell.Clone(cell);
            this.target_cell = Cell.Clone(cell);
            this.move_timestamp = DateTime.MinValue;
            this.is_flip = false;
        }

        public async Task Save()
        {
            await Program.redis_client.BasicRetryAsync(
                (db) =>
                    db.HashSetAsync(
                        HASH_KEY,
                        GetHashField(this.object_type, object_id),
                        JsonSerializer.Serialize(this)
                    )
            );
        }

        public static async Task<GameObjectInfo?> Load(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await Program.redis_client.BasicRetryAsync(
                    db => db.HashGetAsync(HASH_KEY, GetHashField(type, object_id))
                );

                if (serialized_data.IsNull)
                {
                    return null;
                }

                var object_info = JsonSerializer.Deserialize<GameObjectInfo?>(
                    serialized_data.ToString()
                );

                return object_info;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}{e.Message}");
                return null;
            }
        }

        public async Task Delete()
        {
            await Program.redis_client.BasicRetryAsync(
                (db) => db.HashDeleteAsync(HASH_KEY, GetHashField(this.object_type, object_id))
            );
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
