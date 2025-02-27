using MessagePack;
using network.helpers;
using network.interfaces;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class CampInfo
{
    public async Task Save(ICacheHelper cacheHelper)
    {
        await ObjectInfo.Save(cacheHelper);
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<CampInfo?> Load(ICacheHelper cacheHelper, long playerId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var campInfo = MessagePackSerializer.Deserialize<CampInfo?>(serializedData);
        if (campInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(cacheHelper, ObjectType.CAMP, playerId);
        campInfo.ObjectInfo = objectInfo ?? new GameObjectInfo();
        
        return campInfo;
    }

    public async Task Delete(ICacheHelper cacheHelper)
    {
        await cacheHelper.HashDeleteAsync(HashKey, PlayerId);
    }

    public static async Task Delete(ICacheHelper cacheHelper, long playerId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}