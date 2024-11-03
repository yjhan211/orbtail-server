using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;
public partial class JobResourceInfo
{
    public static async Task<IRedLock> Lock(RedLockFactory redLock, long resourceUid)
    {
        return await redLock.CreateLockAsync(GetLockKey(resourceUid), Config.LOCK_TTL);
    }

    public async Task Save()
    {
        await ObjectInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, ResourceUid, MessagePackSerializer.Serialize(this));
    }

    public static async Task<JobResourceInfo?> Load(long resourceUid)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, resourceUid);
        if (serializedData.IsNull) return null;

        var resourceInfo = MessagePackSerializer.Deserialize<JobResourceInfo?>(serializedData);
        if (resourceInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(ObjectType.JOBRESOURCE, resourceUid);
        if (objectInfo == null) return null;

        resourceInfo.ObjectInfo = objectInfo;
        return resourceInfo;
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, ResourceUid);
    }

    public static async Task Delete(long resourceUid)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, resourceUid);
    }
}