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

    public async Task Save(CacheHelper cacheHelper)
    {
        // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
        // await GameObjectInfoController.Save(cache_helper, player_info.object_info);
        await InventoryInfo.Save(cacheHelper);
        await CraftInfo.Save(cacheHelper);
        await CampInfo.Save(cacheHelper);
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<PlayerInfo?> Load(CacheHelper cacheHelper, long playerId)
    {
        var serialized = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serialized == RedisValue.Null)
        {
            return null;
        }

        var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);

        playerInfo.ObjectInfo = await GameObjectInfo.Load(cacheHelper, ObjectType.PLAYER, playerId) ?? new GameObjectInfo(playerId);
        playerInfo.InventoryInfo = await InventoryInfo.Load(cacheHelper, InventoryOwnerType.PLAYER, playerId) ??
                                   new InventoryInfo(InventoryOwnerType.PLAYER, playerId);
        playerInfo.CraftInfo = await CraftInfo.Load(cacheHelper, playerId) ?? new CraftInfo(playerId);
        playerInfo.CampInfo = await CampInfo.Load(cacheHelper, playerId) ?? new CampInfo(playerId, playerInfo.Name, new GameObjectInfo(), new ItemInfo(), new Cell(0, 0));
        playerInfo.IsNew = false;
        
        return playerInfo;
    }

    public static async Task<List<PlayerInfo>> LoadAll(CacheHelper cacheHelper, RedisValue[] objectKeys)
    {
        var hashEntries = await cacheHelper.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<PlayerInfo>(hashString)).ToList();
    }

    public async Task Delete(CacheHelper cacheHelper, PlayerInfo playerInfo)
    {
        await playerInfo.ObjectInfo.Delete(cacheHelper);
        await cacheHelper.HashDeleteAsync(HashKey, playerInfo.PlayerId);
    }

    public static async Task Delete(CacheHelper cacheHelper, long playerId)
    {
        var objectField = GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, playerId);

        await GameObjectInfo.Delete(cacheHelper, objectField);
        await InventoryInfo.Delete(cacheHelper, InventoryOwnerType.PLAYER, playerId);
        await CraftInfo.Delete(cacheHelper, playerId);
        await CampInfo.Delete(cacheHelper, playerId);
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}
