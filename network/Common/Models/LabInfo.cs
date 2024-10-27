using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;

namespace network.common.models;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class LabInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "LabInfo";

    public LabInfo()
    {
        LabId = 0;
        LabName = "";
        MasterPlayerId = 0;
        MasterPlayerName = "";
        MemberDict = new Dictionary<long, string>();
        LabGrade = LabGrade.NONE;
        ResearchInfoDict = new Dictionary<int, ResearchInfo>();
        InventoryInfo = new InventoryInfo();
    }

    public LabInfo(long labId, long masterPlayerId, string masterPlayerName, string labName)
    {
        LabId = labId;
        LabName = labName;
        MasterPlayerId = masterPlayerId;
        MasterPlayerName = masterPlayerName;
        MemberDict = new Dictionary<long, string> { { MasterPlayerId, MasterPlayerName } };
        LabGrade = LabGrade.CLUB;
        ResearchInfoDict = new Dictionary<int, ResearchInfo>();
        InventoryInfo = new InventoryInfo(InventoryOwnerType.LAB, LabId);
    }

    [Key("labId")] public long LabId { get; set; }

    [Key("labName")] public string LabName { get; set; }

    [Key("masterPlayerId")] public long MasterPlayerId { get; set; }

    [Key("masterPlayerName")] public string MasterPlayerName { get; set; }

    [Key("memberDict")] public Dictionary<long, string> MemberDict { get; set; }

    [Key("labGrade")] public LabGrade LabGrade { get; set; }

    [Key("researchInfoDict")] public Dictionary<int, ResearchInfo> ResearchInfoDict { get; set; }

    [Key("inventoryInfo")] public InventoryInfo InventoryInfo { get; set; }

    public string GetLockKey()
    {
        return $"lab_lock_{LabId}";
    }

    public static string GetLockKey(long labId)
    {
        return $"lab_lock_{labId}";
    }

    public static async Task<IRedLock> Lock(RedLockFactory redLock, long labId)
    {
        return await redLock.CreateLockAsync(GetLockKey(labId), Config.LOCK_TTL);
    }

    public async Task Save()
    {
        await InventoryInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, LabId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<LabInfo?> Load(long labId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, labId);
        if (serializedData.IsNull) return null;

        var labInfo = MessagePackSerializer.Deserialize<LabInfo?>(serializedData);
        if (labInfo == null) return null;

        labInfo.InventoryInfo = await InventoryInfo.Load(InventoryOwnerType.LAB, labId) ??
                                new InventoryInfo(InventoryOwnerType.LAB, labId);
        return labInfo;
    }

    public static async Task Delete(long labId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, labId);
    }
}

[MessagePackObject]
public class ResearchInfo : IMessagePackObject
{
    public ResearchInfo()
    {
        ResearchId = 0;
        Level = 0;
        PointDict = new Dictionary<JobType, int>();
    }

    public ResearchInfo(int researchId, int level)
    {
        ResearchId = researchId;
        Level = level;
        PointDict = new Dictionary<JobType, int>();
        InitResearchPoint();
    }

    [Key("researchId")] public int ResearchId { get; set; }

    [Key("level")] public int Level { get; set; }

    [Key("pointDict")] public Dictionary<JobType, int> PointDict { get; set; }

    public void InitResearchPoint()
    {
        PointDict.Clear();

        // 기획 변경 예정
    }
}