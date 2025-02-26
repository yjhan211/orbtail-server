using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class ExploreTargetInfo
{
    // ReSharper disable once UnusedMember.Global
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

    public async Task Save(CacheHelper cacheHelper)
    {
        await ObjectInfo.Save(cacheHelper);
        await cacheHelper.HashSetAsync(HashKey, ExploreTargetUid, MessagePackSerializer.Serialize(this));
    }

    public static async Task<ExploreTargetInfo?> Load(CacheHelper cacheHelper, long exploreTargetUid)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, exploreTargetUid);
        if (serializedData.IsNull) return null;

        var exploreTargetInfo = MessagePackSerializer.Deserialize<ExploreTargetInfo?>(serializedData);
        if (exploreTargetInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(cacheHelper, ObjectType.EXPLORETARGET, exploreTargetUid);
        if (objectInfo == null) return null;

        exploreTargetInfo.ObjectInfo = objectInfo;
        return exploreTargetInfo;
    }

    public async Task Delete(CacheHelper cacheHelper)
    {
        await cacheHelper.HashDeleteAsync(HashKey, ExploreTargetUid);
    }

    public static async Task Delete(CacheHelper cacheHelper, long exploreTargetUid)
    {
        await cacheHelper.HashDeleteAsync(HashKey, exploreTargetUid);
    }
}