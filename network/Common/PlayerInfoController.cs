namespace game_server
{
    using StackExchange.Redis;
    using MessagePack;
    using network;
    using RedLockNet.SERedis;
    using RedLockNet;

    public static class PlayerInfoController
    {
        public static async Task<IRedLock> Lock(RedLockFactory redlock, long player_id)
        {
            return await redlock.CreateLockAsync(PlayerInfo.GetLockKey(player_id), Config.LOCK_TTL);
        }

        public static void Save(CacheHelper cache_helper, PlayerInfo player_info)
        {
            // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
            // await GameObjectInfoController.Save(cache_helper, player_info.object_info);
            JobInfoController.Save(cache_helper, player_info.job_info);
            InventoryInfoController.Save(cache_helper, player_info.inventory_info);

            cache_helper.HashSet(
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

        public static PlayerInfo? Load(CacheHelper cache_helper, long player_id)
        {
            // TODO from DB
            try
            {
                var serialized = cache_helper.HashGet(PlayerInfo.HASH_KEY, player_id);

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
                    GameObjectInfoController.Load(cache_helper, ObjectType.PLAYER, player_id)
                    ?? new GameObjectInfo(player_id);

                player_info.job_info =
                    JobInfoController.Load(cache_helper, player_id) ?? new JobInfo(player_id);

                player_info.inventory_info =
                    InventoryInfoController.Load(cache_helper, InventoryOwnerType.PLAYER, player_id)
                    ?? new InventoryInfo(InventoryOwnerType.PLAYER, player_id);

                return player_info;
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                return null;
            }
        }

        public static List<PlayerInfo> LoadAll(CacheHelper cache_helper, RedisValue[] object_keys)
        {
            try
            {
                var hash_entries = cache_helper.HashGet(PlayerInfo.HASH_KEY, object_keys);
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

        public static void Delete(CacheHelper cache_helper, PlayerInfo player_info)
        {
            GameObjectInfoController.Delete(cache_helper, player_info.object_info);
            cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_info.player_id);
        }

        public static void Delete(CacheHelper cache_helper, long player_id)
        {
            GameObjectInfoController.Delete(
                cache_helper,
                GameObjectInfo.MakeHashField(ObjectType.PLAYER, player_id)
            );

            JobInfoController.Delete(cache_helper, player_id);

            cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_id);
        }
    }
}
