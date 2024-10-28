using StackExchange.Redis;
using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class GameObjectInfo
{
    public static async Task<GameObjectInfo?> Load(ObjectType type, long objectId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, MakeObjectKey(type, objectId));
        if (serializedData.IsNull) return null;

        return MessagePackSerializer.Deserialize<GameObjectInfo?>(serializedData);
    }

    public static async Task<List<GameObjectInfo>> LoadAll(RedisValue[] objectKeys)
    {
        var hashEntries = await CacheHelper.Instance.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<GameObjectInfo>(hashString)).ToList();
    }

    public static async Task Delete(string hashField)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, hashField);
    }

    public static async Task<bool> Exist(string hashField)
    {
        return await CacheHelper.Instance.HashExistsAsync(HashKey, hashField);
    }

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, GetGameObjectKey(), MessagePackSerializer.Serialize(this));
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, GetGameObjectKey());
    }
}