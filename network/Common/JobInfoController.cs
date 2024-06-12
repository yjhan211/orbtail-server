namespace network
{
    using MessagePack;

    public static class JobInfoController
    {
        public static void Save(CacheHelper cache_helper, JobInfo job_info)
        {
            cache_helper.HashSet(
                JobInfo.HASH_KEY,
                job_info.player_id,
                MessagePackSerializer.Serialize(job_info)
            );
        }

        public static JobInfo? Load(CacheHelper cache_helper, long player_id)
        {
            var serialized_data = cache_helper.HashGet(JobInfo.HASH_KEY, player_id);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var job_info = MessagePackSerializer.Deserialize<JobInfo?>(serialized_data);
            return job_info;
        }

        public static void Delete(CacheHelper cache_helper, long player_id)
        {
            cache_helper.HashDelete(JobInfo.HASH_KEY, player_id);
        }
    }
}
