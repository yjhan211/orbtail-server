using MessagePack;
using network.helpers;
using network.interfaces;
using RedLockNet;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class PlayerInfo
{
    public static async Task<IRedLock> Lock(IRedLockFactory redLock, long playerId)
    {
        return await redLock.CreateLockAsync(GetLockKey(playerId), Config.LOCK_TTL);
    }

    public async Task<IRedLock> Lock(IRedLockFactory redLock)
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
            // Cell → World Position 변환 (Unity Isometric Z as Y 타일맵)
            playerInfo.ObjectInfo.Position = CellToWorldPosition(playerInfo.LastCell);
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

    /// <summary>
    /// Cell 좌표를 Unity Isometric World Position으로 변환
    /// 공식: WorldX = (GridCellX - GridCellY) * 0.5
    ///       WorldY = (GridCellX + GridCellY) * 0.25 + 0.25
    /// </summary>
    private static Vector3f CellToWorldPosition(Cell cell)
    {
        // Unity MapController CellOffset
        const int cellOffsetX = -5;
        const int cellOffsetY = -5;
        const float cellCenterOffsetY = 0.25f;

        // CellOffset 적용
        int gridCellX = cell.X + cellOffsetX;
        int gridCellY = cell.Y + cellOffsetY;

        float worldX = (gridCellX - gridCellY) * 0.5f;
        float worldY = (gridCellX + gridCellY) * 0.25f + cellCenterOffsetY;

        return new Vector3f(worldX, worldY, 0);
    }
}
