// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class JobInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "JobInfo";

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobInfo()
        {
            PlayerId = 0;
            JobStatDict = new Dictionary<JobType, JobStat>();
            ResearchPointDict = new Dictionary<JobType, ResearchPoint>();
        }

        public JobInfo(long playerId)
        {
            PlayerId = playerId;
            JobStatDict = new Dictionary<JobType, JobStat>();
            ResearchPointDict = new Dictionary<JobType, ResearchPoint>();
            JobStatDict[JobType.ENGINEER] = new JobStat();
            JobStatDict[JobType.CHEMIST] = new JobStat();
            ResearchPointDict[JobType.ENGINEER] = new ResearchPoint();
            ResearchPointDict[JobType.CHEMIST] = new ResearchPoint();
        }

        [Key("playerId")] public long PlayerId { get; set; }

        [Key("jobStatDict")] public Dictionary<JobType, JobStat> JobStatDict { get; set; }

        [Key("researchPointDict")] public Dictionary<JobType, ResearchPoint> ResearchPointDict { get; set; }
    }

    [MessagePackObject]
    public class JobStat : IMessagePackObject
    {
        [Key("grade")] public JobGrade JobGrade { get; set; } = JobGrade.TRAINEE;

        [Key("exp")] public int Exp { get; set; }
    }

    [MessagePackObject]
    public class ResearchPoint : IMessagePackObject
    {
        [Key("point")] public long Point { get; set; }

        [Key("usePoint")] public long UsePoint { get; set; }
    }
}