namespace game_server
{
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public static class GameObjecController
    {
        public static async Task Save(GameObjectInfo object_info)
        {
            await Program.cache_helper.HashSet(
                GameObjectInfo.HASH_KEY,
                object_info.GetHashField(),
                MessagePackSerializer.Serialize(object_info)
            );
        }

        // public void Save(Transaction transaction)
        // {
        //     transaction.HashSet(
        //         HASH_KEY,
        //         this.GetHashField(),
        //         MessagePackSerializer.Serialize(this)
        //     );
        // }

        public static async Task<GameObjectInfo?> Load(ObjectType type, long object_id)
        {
            try
            {
                var serialized_data = await Program.cache_helper.HashGet(
                    GameObjectInfo.HASH_KEY,
                    GameObjectInfo.MakeHashField(type, object_id)
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
                var hash_entries = await Program.cache_helper.HashGet(
                    GameObjectInfo.HASH_KEY,
                    object_keys
                );

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

        public static async Task Delete(GameObjectInfo object_info)
        {
            await Program.cache_helper.HashDelete(
                GameObjectInfo.HASH_KEY,
                object_info.GetHashField()
            );
        }
    }
}
