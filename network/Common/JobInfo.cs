using MessagePack;
using network.helpers;
using System.Diagnostics.CodeAnalysis;
namespace network.common;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class JobInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "JobInfo";

    // 이거 없애면 안됨 MessagePack에서 씀
    public JobInfo()
    {
        PlayerId = 0;
        JobStatDict = new Dictionary<JobType, JobStat>();
        ResearchPointDict = new Dictionary<JobType, ResearchPoint>();
        Hp = 0;
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
        Hp = 100;
    }

    [Key("playerId")] public long PlayerId { get; set; }

    [Key("jobStatDict")] public Dictionary<JobType, JobStat> JobStatDict { get; set; }

    [Key("researchPointDict")] public Dictionary<JobType, ResearchPoint> ResearchPointDict { get; set; }

    [Key("hp")] public int Hp { get; set; }

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<JobInfo?> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var jobInfo = MessagePackSerializer.Deserialize<JobInfo?>(serializedData);
        return jobInfo;
    }

    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
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