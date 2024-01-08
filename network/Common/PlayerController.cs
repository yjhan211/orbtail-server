namespace game_server
{
    using StackExchange.Redis;
    using MessagePack;
    using network;

    public static class PlayerController
    {
        public static async Task Save(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjectController.Save(cache_helper, player_info.object_info);
            await cache_helper.HashSet(
                PlayerInfo.HASH_KEY,
                player_info.player_id,
                MessagePackSerializer.Serialize(player_info)
            );
        }

        // public void Save(Transaction transaction)
        // {
        //     this.object_info.Save(transaction);
        //     transaction.HashSet(HASH_KEY, this.player_id, MessagePackSerializer.Serialize(this));
        // }

        public static async Task<PlayerInfo?> Load(CacheHelper cache_helper, long player_id)
        {
            try
            {
                var serialized = await cache_helper.HashGet(PlayerInfo.HASH_KEY, player_id);

                if (serialized == RedisValue.Null)
                {
                    return null;
                }

                var player_info = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);
                if (player_info == null)
                {
                    return null;
                }

                var object_info = await GameObjectController.Load(
                    cache_helper,
                    ObjectType.PLAYER,
                    player_id
                );

                if (object_info == null)
                {
                    return null;
                }

                player_info.object_info = object_info;
                return player_info;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.Message} {e.StackTrace}");
                return null;
            }
        }

        public static async Task<List<PlayerInfo>> LoadAll(
            CacheHelper cache_helper,
            RedisValue[] object_keys
        )
        {
            try
            {
                var hash_entries = await cache_helper.HashGet(PlayerInfo.HASH_KEY, object_keys);
                if (hash_entries == null)
                {
                    return new();
                }

                List<RedisValue> hash_strings = hash_entries
                    .Where(entry => entry != RedisValue.Null)
                    .Select(entry => entry)
                    .ToList();

                List<PlayerInfo> result = new();

                foreach (var hash_string in hash_strings)
                {
                    var player_info = MessagePackSerializer.Deserialize<PlayerInfo>(hash_string);
                    if (player_info == null)
                    {
                        continue;
                    }

                    result.Add(player_info);
                }

                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.StackTrace}{e.Message}");
                return new();
            }
        }

        public static async Task Delete(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjectController.Delete(cache_helper, player_info.object_info);
            await cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_info.player_id);
        }
    }
}
