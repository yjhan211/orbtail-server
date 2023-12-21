namespace game_server
{
    using MessagePack;
    using StackExchange.Redis;

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
            await CacheHelper.HashSet(
                HASH_KEY,
                this.GetHashField(),
                MessagePackSerializer.Serialize(this)
            );
        }

        public void Save(Transaction transaction)
        {
            transaction.HashSet(
                HASH_KEY,
                this.GetHashField(),
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<GameObjectInfo?> Load(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await CacheHelper.HashGet(
                    HASH_KEY,
                    MakeHashField(type, object_id)
                );

                if (serialized_data.IsNull)
                {
                    return null;
                }

                var object_info = MessagePackSerializer.Deserialize<GameObjectInfo?>(
                    serialized_data
                );

                return object_info;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.StackTrace}{e.Message}");
                return null;
            }
        }

        public static async Task<List<GameObjectInfo>> LoadAll(RedisValue[] object_keys)
        {
            try
            {
                var hash_entries = await CacheHelper.HashGet(HASH_KEY, object_keys);
                if (hash_entries == null)
                {
                    return new();
                }

                List<RedisValue> hash_strings = hash_entries
                    .Where(entry => entry != RedisValue.Null)
                    .Select(entry => entry)
                    .ToList();

                List<GameObjectInfo> result = new();

                foreach (var hash_string in hash_strings)
                {
                    var game_object_info = MessagePackSerializer.Deserialize<GameObjectInfo>(
                        hash_string
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
            await CacheHelper.HashDelete(HASH_KEY, this.GetHashField());
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
