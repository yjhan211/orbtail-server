namespace game_server
{
    using MessagePack;
    using Microsoft.Extensions.Caching.Memory;
    using network;
    using StackExchange.Redis;

    public static class GameObjectController
    {
        public static async Task Save(CacheHelper cache_helper, GameObjectInfo object_info)
        {
            await cache_helper.HashSet(
                GameObjectInfo.HASH_KEY,
                object_info.GetHashField(),
                MessagePackSerializer.Serialize(object_info)
            );
        }

        public static async Task<GameObjectInfo?> Load(
            CacheHelper cache_helper,
            ObjectType type,
            long object_id
        )
        {
            var serialized_data = await cache_helper.HashGet(
                GameObjectInfo.HASH_KEY,
                GameObjectInfo.MakeHashField(type, object_id)
            );

            if (serialized_data.IsNull)
            {
                return null;
            }

            var object_info = MessagePackSerializer.Deserialize<GameObjectInfo?>(serialized_data);
            return object_info;
        }

        public static async Task<List<GameObjectInfo>> LoadAll(
            CacheHelper cache_helper,
            RedisValue[] object_keys
        )
        {
            var hash_entries = await cache_helper.HashGet(GameObjectInfo.HASH_KEY, object_keys);

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

        public static async Task Delete(CacheHelper cache_helper, GameObjectInfo object_info)
        {
            await cache_helper.HashDelete(GameObjectInfo.HASH_KEY, object_info.GetHashField());
        }

        public static async Task Delete(CacheHelper cache_helper, string hash_field)
        {
            await cache_helper.HashDelete(GameObjectInfo.HASH_KEY, hash_field);
        }

        // public void Save(Transaction transaction)
        // {
        //     transaction.HashSet(
        //         HASH_KEY,
        //         this.GetHashField(),
        //         MessagePackSerializer.Serialize(this)
        //     );
        // }
    }
}
