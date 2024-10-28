using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class LabInfo
{
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