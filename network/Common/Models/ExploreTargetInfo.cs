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
public class ExploreTargetInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "ExploreTargetInfo";

    // 이거 없애면 안됨 MessagePack에서 씀
    public ExploreTargetInfo()
    {
        ObjectInfo = new GameObjectInfo();
        ExploreTargetUid = 0;
        ExploreTargetId = 0;
        PlayerId = 0;
    }

    public ExploreTargetInfo(long exploreTargetUid, int exploreTargetId, GameObjectInfo objectInfo)
    {
        ObjectInfo = objectInfo;
        ExploreTargetUid = exploreTargetUid;
        ExploreTargetId = exploreTargetId;
        PlayerId = 0;
    }

    [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

    [Key("exploreTargetUid")] public long ExploreTargetUid { get; set; }

    [Key("exploreTargetId")] public int ExploreTargetId { get; set; }

    [Key("playerId")] public long PlayerId { get; set; } // 점유중인 플레이어 아이디

    [Key("endTimestamp")] public DateTime EndTimestamp { get; set; } // 점유 끝나는 시간

    public string GetLockKey()
    {
        return $"explore_target_lock_{ExploreTargetUid}";
    }

    private static string GetLockKey(long exploreTargetUid)
    {
        return $"explore_target_lock_{exploreTargetUid}";
    }

    public static async Task<IRedLock> Lock(RedLockFactory redLock, long exploreTargetUid)
    {
        return await redLock.CreateLockAsync(GetLockKey(exploreTargetUid), Config.LOCK_TTL);
    }

    public async Task Save()
    {
        await ObjectInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, ExploreTargetUid, MessagePackSerializer.Serialize(this));
    }

    public static async Task<ExploreTargetInfo?> Load(long exploreTargetUid)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, exploreTargetUid);
        if (serializedData.IsNull) return null;

        var exploreTargetInfo = MessagePackSerializer.Deserialize<ExploreTargetInfo?>(serializedData);
        if (exploreTargetInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(ObjectType.EXPLORETARGET, exploreTargetUid);
        if (objectInfo == null) return null;

        exploreTargetInfo.ObjectInfo = objectInfo;
        return exploreTargetInfo;
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, ExploreTargetUid);
    }

    public static async Task Delete(long exploreTargetUid)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, exploreTargetUid);
    }
}