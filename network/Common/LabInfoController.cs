namespace network
{
    using MessagePack;
    using RedLockNet.SERedis;
    using RedLockNet;

    public static class LabInfoController
    {
        public static async Task<IRedLock> Lock(RedLockFactory redlock, long lab_id)
        {
            return await redlock.CreateLockAsync(LabInfo.GetLockKey(lab_id), Config.LOCK_TTL);
        }

        public static async Task Save(CacheHelper cache_helper, LabInfo lab_info)
        {
            await cache_helper.HashSet(
                LabInfo.HASH_KEY,
                lab_info.lab_id,
                MessagePackSerializer.Serialize(lab_info)
            );
        }

        public static async Task<LabInfo?> Load(CacheHelper cache_helper, long player_id)
        {
            var serialized_data = await cache_helper.HashGet(LabInfo.HASH_KEY, player_id);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var lab_info = MessagePackSerializer.Deserialize<LabInfo?>(serialized_data);
            return lab_info;
        }

        public static async Task Delete(CacheHelper cache_helper, long lab_id)
        {
            await cache_helper.HashDelete(LabInfo.HASH_KEY, lab_id);
        }
    }
}
