using MessagePack;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class JobInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "JobInfo";

        [Key("playerId")]
        public long PlayerId { get; set; }

        [Key("jobStatDict")]
        public Dictionary<JobType, JobStat> JobStatDict { get; set; }

        [Key("researchPointDict")]
        public Dictionary<JobType, ReserachPoint> ResearchPointDict { get; set; }

        [Key("hp")]
        public int Hp { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobInfo()
        {
            PlayerId = 0;
            JobStatDict = new();
            ResearchPointDict = new();
            Hp = 0;
        }

        public JobInfo(long playerId)
        {
            PlayerId = playerId;
            JobStatDict = new();
            ResearchPointDict = new();
            JobStatDict[JobType.ENGINEER] = new();
            JobStatDict[JobType.CHEMIST] = new();
            ResearchPointDict[JobType.ENGINEER] = new();
            ResearchPointDict[JobType.CHEMIST] = new();
            Hp = 100;
        }

        public async Task Save()
        {
            await CacheHelper.Instance.HashSetAsync(JobInfo.HASH_KEY, PlayerId, MessagePackSerializer.Serialize(this));
        }

        public static async Task<JobInfo?> Load(long playerId)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(JobInfo.HASH_KEY, playerId);
            if (serializedData.IsNull)
            {
                return null;
            }

            var jobInfo = MessagePackSerializer.Deserialize<JobInfo?>(serializedData);
            return jobInfo;
        }

        public static async Task Delete(long playerId)
        {
            await CacheHelper.Instance.HashDeleteAsync(JobInfo.HASH_KEY, playerId);
        }
    }

    [MessagePackObject]
    public class JobStat : IMessagePackObject
    {
        [Key("grade")]
        public JobGrade JobGrade { get; set; }

        [Key("exp")]
        public int Exp { get; set; }

        public JobStat()
        {
            JobGrade = JobGrade.TRAINEE;
            Exp = 0;
        }
    }

    [MessagePackObject]
    public class ReserachPoint : IMessagePackObject
    {
        [Key("point")]
        public long Point { get; set; }

        [Key("usePoint")]
        public long UsePoint { get; set; }
    }
}
