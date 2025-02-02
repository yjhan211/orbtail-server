using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class CraftInfo
{
    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }
    
    public static async Task<CraftInfo?> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var craftInfo = MessagePackSerializer.Deserialize<CraftInfo?>(serializedData);
        return craftInfo;
    }

    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}