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

        [Key("hp")]
        public int hp { get; set; }

        [Key("exp")]
        public int exp { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobInfo()
        {
            this.player_id = 0;
            this.job_type = JobType.NONE;
            this.job_grade = JobGrade.NONE;
            this.hp = 0;
            this.exp = 0;
        }

        public JobInfo(long player_id)
        {
            this.player_id = player_id;
            this.job_type = JobType.NONE;
            this.job_grade = JobGrade.NONE;
            this.hp = 0;
            this.exp = 0;
        }
    }
}
