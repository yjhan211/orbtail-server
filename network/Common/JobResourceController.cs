namespace network
{
    using game_server;
    using MessagePack;
    using RedLockNet.SERedis;
    using RedLockNet;

    public static class JobResourceController
    {
        public static async Task<IRedLock> Lock(RedLockFactory redlock, long resource_uid)
        {
            return await redlock.CreateLockAsync(
                JobResourceInfo.GetLockKey(resource_uid),
                Config.LOCK_TTL
            );
        }

        public static void Save(CacheHelper cache_helper, JobResourceInfo job_resource_info)
        {
            GameObjectInfoController.Save(cache_helper, job_resource_info.object_info);

            cache_helper.HashSet(
                JobResourceInfo.HASH_KEY,
                job_resource_info.resource_uid,
                MessagePackSerializer.Serialize(job_resource_info)
            );
        }

        public static JobResourceInfo? Load(CacheHelper cache_helper, long resource_uid)
        {
            var serialized_data = cache_helper.HashGet(JobResourceInfo.HASH_KEY, resource_uid);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var resource_info = MessagePackSerializer.Deserialize<JobResourceInfo?>(
                serialized_data
            );

            if (resource_info == null)
            {
                return null;
            }

            var object_info = GameObjectInfoController.Load(
                cache_helper,
                ObjectType.JOBRESOURCE,
                resource_uid
            );

            // 없으면 버그
            if (object_info == null)
            {
                return null;
            }

            resource_info.object_info = object_info;

            return resource_info;
        }

        public static void Delete(CacheHelper cache_helper, long resource_uid)
        {
            cache_helper.HashDelete(JobResourceInfo.HASH_KEY, resource_uid);
        }
    }
}
