using MessagePack;
using network.helpers;
using network.interfaces;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class PlayerInfo
{
    public static async Task<IRedLock> Lock(IRedLockFactory redLock, long playerId)
    {
        return await redLock.CreateLockAsync(GetLockKey(playerId), Config.LOCK_TTL);
    }

    public async Task<IRedLock> Lock(RedLockFactory redLock)
    {
        return await redLock.CreateLockAsync(GetLockKey(), Config.LOCK_TTL);
    }

    public async Task Save(ICacheHelper cacheHelper)
    {
        // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
        // await GameObjectInfoController.Save(cache_helper, player_info.object_info);
        await InventoryInfo.Save(cacheHelper);
        await QuestDiary.Save(cacheHelper);
        await MailBox.Save(cacheHelper);
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<PlayerInfo?> Load(ICacheHelper cacheHelper, long playerId)
    {
        var serialized = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serialized == RedisValue.Null)
        {
            return null;
        }

        var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);

        playerInfo.ObjectInfo = await GameObjectInfo.Load(cacheHelper, ObjectType.PLAYER, playerId) ?? new GameObjectInfo(playerId);
        playerInfo.InventoryInfo = await InventoryInfo.Load(cacheHelper, InventoryOwnerType.PLAYER, playerId) ?? new InventoryInfo(InventoryOwnerType.PLAYER, playerId);
        playerInfo.QuestDiary = await QuestDiary.Load(cacheHelper, playerId);
        playerInfo.MailBox = await MailBox.Load(cacheHelper, playerId);
        playerInfo.IsNew = false;

        // ObjectInfo의 Cell, MapId, MapSubId를 LastCell, LastMapId, LastMapSubId로 동기화 (세션 기반 게임)
        if (playerInfo.LastCell != null)
        {
            playerInfo.ObjectInfo.Cell = playerInfo.LastCell;
            playerInfo.ObjectInfo.Position = new Vector3f(playerInfo.LastCell.X, 0, playerInfo.LastCell.Y);
        }
        playerInfo.ObjectInfo.MapId = playerInfo.LastMapId;
        playerInfo.ObjectInfo.MapSubId = playerInfo.LastMapSubId;

        return playerInfo;
    }

    public static async Task<List<PlayerInfo>> LoadAll(ICacheHelper cacheHelper, RedisValue[] objectKeys)
    {
        var hashEntries = await cacheHelper.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<PlayerInfo>(hashString)).ToList();
    }

    public async Task Delete(ICacheHelper cacheHelper, PlayerInfo playerInfo)
    {
        await playerInfo.ObjectInfo.Delete(cacheHelper);
        await cacheHelper.HashDeleteAsync(HashKey, playerInfo.PlayerId);
    }

    public static async Task Delete(ICacheHelper cacheHelper, long playerId)
    {
        var objectField = GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, playerId);

        await GameObjectInfo.Delete(cacheHelper, objectField);
        await InventoryInfo.Delete(cacheHelper, InventoryOwnerType.PLAYER, playerId);
        await QuestDiary.Delete(cacheHelper, playerId);
        await MailBox.Delete(cacheHelper, playerId);
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}
