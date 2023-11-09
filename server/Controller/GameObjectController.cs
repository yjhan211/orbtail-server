namespace game_server
{
    using MessagePack;
    using StackExchange.Redis;
    using System.Text.Json;

    public partial class GameObjectInfo
    {
        public static string MakeHashField(ObjectType type, long object_id)
        {
            return $"{(int)type}_{object_id}";
        }

        public string GetHashField()
        {
            return $"{(int)this.object_type}_{this.object_id}";
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
            await RedisHelper.HashSet(
                HASH_KEY,
                this.GetHashField(),
                JsonSerializer.Serialize(this)
            );
        }

        public static async Task<GameObjectInfo?> Load(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await RedisHelper.HashGet(
                    HASH_KEY,
                    MakeHashField(type, object_id)
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

        public static async Task<List<GameObjectInfo>> LoadAll(List<string> object_key_list)
        {
            try
            {
                var hash_entries = await RedisHelper.HashGet(HASH_KEY, object_key_list);
                if (hash_entries == null)
                {
                    return new();
                }

                List<string> hash_strings = hash_entries
                    .Where(entry => entry != RedisValue.Null)
                    .Select(entry => entry.ToString())
                    .ToList();

                List<GameObjectInfo> result = new();

                for (int i = 0; i < hash_strings.Count; i++)
                {
                    var game_object_info = JsonSerializer.Deserialize<GameObjectInfo>(
                        hash_strings[i]
                    );

                    if (game_object_info == null)
                    {
                        continue;
                    }

                    result.Add(game_object_info);
                }

                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}{e.Message}");
                return new();
            }
        }

        public async Task Delete()
        {
            await RedisHelper.HashDelete(HASH_KEY, this.GetHashField());
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
