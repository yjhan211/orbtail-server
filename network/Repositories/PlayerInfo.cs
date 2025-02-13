using MessagePack;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class PlayerInfo
{
    public static async Task<IRedLock> Lock(RedLockFactory redLock, long playerId)
    {
        return await redLock.CreateLockAsync(GetLockKey(playerId), Config.LOCK_TTL);
    }

    public async Task<IRedLock> Lock(RedLockFactory redLock)
    {
        return await redLock.CreateLockAsync(GetLockKey(), Config.LOCK_TTL);
    }

    public async Task Save()
    {
        // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
        // await GameObjectInfoController.Save(cache_helper, player_info.object_info);

        await JobInfo.Save();
        await InventoryInfo.Save();
        await CraftInfo.Save();
        await CampInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<PlayerInfo?> Load(long playerId)
    {
        var serialized = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serialized == RedisValue.Null) return null;

        var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);

        playerInfo.ObjectInfo = await GameObjectInfo.Load(ObjectType.PLAYER, playerId) ?? new GameObjectInfo(playerId);
        playerInfo.JobInfo = await JobInfo.Load(playerId) ?? new JobInfo(playerId);
        playerInfo.InventoryInfo = await InventoryInfo.Load(InventoryOwnerType.PLAYER, playerId) ??
                                   new InventoryInfo(InventoryOwnerType.PLAYER, playerId);
        playerInfo.CraftInfo = await CraftInfo.Load(playerId) ?? new CraftInfo(playerId);
        playerInfo.CampInfo = await CampInfo.Load(playerId) ?? new CampInfo(playerId, playerInfo.Name, new GameObjectInfo(), new(), new(0, 0));

        return playerInfo;
    }

    public static async Task<List<PlayerInfo>> LoadAll(RedisValue[] objectKeys)
    {
        var hashEntries = await CacheHelper.Instance.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<PlayerInfo>(hashString)).ToList();
    }

    public async Task Delete(PlayerInfo playerInfo)
    {
        await playerInfo.ObjectInfo.Delete();
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerInfo.PlayerId);
    }

    public static async Task Delete(long playerId)
    {
        var objectField = GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, playerId);

        await GameObjectInfo.Delete(objectField);
        await JobInfo.Delete(playerId);
        await InventoryInfo.Delete(InventoryOwnerType.PLAYER, playerId);
        await CraftInfo.Delete(playerId);
        await CampInfo.Delete(playerId);
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}