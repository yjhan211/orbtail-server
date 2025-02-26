using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class LabInfo
{
    public static async Task<IRedLock> Lock(CacheHelper cacheHelper, long labId)
    {
        return await cacheHelper.GetRedLockFactory().CreateLockAsync(GetLockKey(labId), Config.LOCK_TTL);
    }

    public async Task Save(CacheHelper cacheHelper)
    {
        await InventoryInfo.Save(cacheHelper);
        await cacheHelper.HashSetAsync(HashKey, LabId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<LabInfo?> Load(CacheHelper cacheHelper, long labId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, labId);
        if (serializedData.IsNull) return null;

        var labInfo = MessagePackSerializer.Deserialize<LabInfo?>(serializedData);
        if (labInfo == null) return null;

        labInfo.InventoryInfo = await InventoryInfo.Load(cacheHelper, InventoryOwnerType.LAB, labId) ??
                                new InventoryInfo(InventoryOwnerType.LAB, labId);
        return labInfo;
    }

    public static async Task Delete(CacheHelper cacheHelper, long labId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, labId);
    }
}
