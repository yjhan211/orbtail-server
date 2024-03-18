namespace game_server
{
    using StackExchange.Redis;
    using MessagePack;
    using network;
    using RedLockNet.SERedis;
    using RedLockNet;
    using network.Common;
    using Newtonsoft.Json.Linq;

    public static class PlayerInfoController
    {
        public static async Task<IRedLock> Lock(RedLockFactory redlock, long player_id)
        {
            return await redlock.CreateLockAsync(PlayerInfo.GetLockKey(player_id), Config.LOCK_TTL);
        }

        public static async Task Save(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjectInfoController.Save(cache_helper, player_info.object_info);
            await JobInfoController.Save(cache_helper, player_info.job_info);
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
            // TODO from DB
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

                player_info.object_info =
                    await GameObjectInfoController.Load(cache_helper, ObjectType.PLAYER, player_id)
                    ?? new GameObjectInfo();

                player_info.job_info =
                    await JobInfoController.Load(cache_helper, player_id) ?? new JobInfo();

                return player_info;
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
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
                LogManager.WriteErrorLog(e);
                return new();
            }
        }

        public static async Task Delete(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjectInfoController.Delete(cache_helper, player_info.object_info);
            await cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_info.player_id);
        }

        public static async Task Delete(CacheHelper cache_helper, long player_id)
        {
            await GameObjectInfoController.Delete(
                cache_helper,
                GameObjectInfo.MakeHashField(ObjectType.PLAYER, player_id)
            );

            await JobInfoController.Delete(cache_helper, player_id);

            await cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_id);
        }
    }
}
