using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class CraftInfo
{
    public async Task Save(CacheHelper cacheHelper)
    {
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }
    
    public static async Task<CraftInfo?> Load(CacheHelper cacheHelper, long playerId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var craftInfo = MessagePackSerializer.Deserialize<CraftInfo?>(serializedData);
        return craftInfo;
    }

    public static async Task Delete(CacheHelper cacheHelper, long playerId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}