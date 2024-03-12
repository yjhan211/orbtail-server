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

        [Key("job_type")]
        public JobType job_type { get; set; }

        [Key("grade")]
        public JobGrade job_grade { get; set; }

        [Key("exp")]
        public int exp { get; set; }

        public JobInfo()
        {
            this.player_id = 0;

            this.job_type = JobType.NONE;
            this.job_grade = JobGrade.NONE;
            this.exp = 0;
        }
    }
}
