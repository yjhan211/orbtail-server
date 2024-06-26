namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class JobInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "job_info";

        [Key("player_id")]
        public long player_id { get; set; }

        [Key("job_stat_dict")]
        public Dictionary<JobType, JobStat> job_stat_dict { get; set; }

        [Key("research_point_dict")]
        public Dictionary<JobType, long> research_point_dict { get; set; }

        [Key("hp")]
        public int hp { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobInfo()
        {
            this.player_id = 0;
            this.job_stat_dict = new();
            this.research_point_dict = new();
            this.hp = 0;
        }

        public JobInfo(long player_id)
        {
            this.player_id = player_id;
            this.job_stat_dict = new();
            this.research_point_dict = new();

            this.job_stat_dict[JobType.ENGINEER] = new();
            this.job_stat_dict[JobType.CHEMIST] = new();

            this.research_point_dict[JobType.ENGINEER] = 0;
            this.research_point_dict[JobType.CHEMIST] = 0;

            this.hp = 0;
        }

        public async Task Save()
        {
            await CacheHelper.Instance.HashSetAsync(
                JobInfo.HASH_KEY,
                this.player_id,
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<JobInfo?> Load(long player_id)
        {
            var serialized_data = await CacheHelper.Instance.HashGetAsync(
                JobInfo.HASH_KEY,
                player_id
            );

            if (serialized_data.IsNull)
            {
                return null;
            }

            var job_info = MessagePackSerializer.Deserialize<JobInfo?>(serialized_data);
            return job_info;
        }

        public static async Task Delete(long player_id)
        {
            await CacheHelper.Instance.HashDeleteAsync(JobInfo.HASH_KEY, player_id);
        }
    }

    [MessagePackObject]
    public class JobStat : IMessagePackObject
    {
        [Key("grade")]
        public JobGrade job_grade { get; set; }

        [Key("exp")]
        public int exp { get; set; }

        public JobStat()
        {
            job_grade = JobGrade.TRAINEE;
            exp = 0;
        }
    }
}
