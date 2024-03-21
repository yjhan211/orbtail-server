namespace network
{
    using MessagePack;

    public static class JobInfoController
    {
        public static async Task Save(CacheHelper cache_helper, JobInfo job_info)
        {
            await cache_helper.HashSet(
                JobInfo.HASH_KEY,
                job_info.player_id,
                MessagePackSerializer.Serialize(job_info)
            );
        }

        public static async Task<JobInfo?> Load(CacheHelper cache_helper, long player_id)
        {
            var serialized_data = await cache_helper.HashGet(JobInfo.HASH_KEY, player_id);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var job_info = MessagePackSerializer.Deserialize<JobInfo?>(serialized_data);
            return job_info;
        }

        public static async Task Delete(CacheHelper cache_helper, long player_id)
        {
            await cache_helper.HashDelete(JobInfo.HASH_KEY, player_id);
        }
    }
}
