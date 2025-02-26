using StackExchange.Redis;
using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class GameObjectInfo
{
    public static async Task<GameObjectInfo?> Load(CacheHelper cacheHelper, ObjectType type, long objectId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, MakeObjectKey(type, objectId));
        if (serializedData.IsNull) return null;

        return MessagePackSerializer.Deserialize<GameObjectInfo?>(serializedData);
    }

    public static async Task<List<GameObjectInfo>> LoadAll(CacheHelper cacheHelper, RedisValue[] objectKeys)
    {
        var hashEntries = await cacheHelper.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<GameObjectInfo>(hashString)).ToList();
    }

    public static async Task Delete(CacheHelper cacheHelper, string hashField)
    {
        await cacheHelper.HashDeleteAsync(HashKey, hashField);
    }

    public static async Task<bool> Exist(CacheHelper cacheHelper, string hashField)
    {
        return await cacheHelper.HashExistsAsync(HashKey, hashField);
    }

    public async Task Save(CacheHelper cacheHelper)
    {
        await cacheHelper.HashSetAsync(HashKey, GetGameObjectKey(), MessagePackSerializer.Serialize(this));
    }

    public async Task Delete(CacheHelper cacheHelper)
    {
        await cacheHelper.HashDeleteAsync(HashKey, GetGameObjectKey());
    }
}