using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class CampInfo
{
    public async Task Save()
    {
        await ObjectInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<CampInfo?> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var campInfo = MessagePackSerializer.Deserialize<CampInfo?>(serializedData);
        if (campInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(ObjectType.CAMP, playerId);
        campInfo.ObjectInfo = objectInfo ?? new GameObjectInfo();
        
        return campInfo;
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, PlayerId);
    }

    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}