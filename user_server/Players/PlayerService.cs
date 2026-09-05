using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.infrastructure.redis;

namespace user_server.players;

/// <summary>
///     Redis PlayerInfo를 player lock 안에서 load/mutate/save하여 아이템 착용과 소비 아이템 사용을 처리한다.
///     연결, 매칭, 퀘스트, 메일 수명주기는 소유하지 않는다.
/// </summary>
public class PlayerService(ILogger<PlayerService> logger, IRedisOperations redisOperations, IRedLockFactory redLock)
    : IPlayerService
{
    // ========== 인벤토리 ==========

    public async Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(redisOperations, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);

            // 기존 착용 아이템 해제
            foreach (var item in playerInfo.InventoryInfo.ItemDict.Values) item.IsWear = false;
            playerInfo.WearItemIdList.Clear();

            // 새 아이템 착용
            foreach (long itemUid in msg.ItemUidList)
                if (playerInfo.InventoryInfo.ItemDict.TryGetValue(itemUid, out var item))
                {
                    item.IsWear = true;
                    playerInfo.WearItemIdList.Add(item.ItemId); // ItemUid가 아니라 ItemId 저장
                }
                else
                {
                    logger.LogWarning(
                        "Player {PlayerId} tried to wear non-existent item UID: {ItemUid}", playerId, itemUid);
                }

            logger.LogInformation("Player {PlayerId} wearing items: {Join}", playerId,
                string.Join(", ", msg.ItemUidList));

            await playerInfo.Save(redisOperations);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WearItem 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    public async Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(redisOperations, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);

            // 아이템 존재 확인
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(msg.ItemUid, out var itemInfo))
            {
                logger.LogWarning($"Player {playerId} tried to use non-existent item UID: {msg.ItemUid}");
                return (ErrorCode.INVALID_ITEM, playerInfo);
            }

            // CONSUMABLE 타입만 사용 가능
            var itemType = GameItemData.GetItemType(itemInfo.ItemId);
            if (itemType != ItemType.CONSUMABLE)
            {
                logger.LogWarning(
                    "Player {PlayerId} tried to use non-consumable item: ItemId={ItemInfoItemId}, Type={ItemType}",
                    playerId, itemInfo.ItemId, itemType);
                return (ErrorCode.INVALID_ITEM_TYPE, playerInfo);
            }

            // TODO: 소비 아이템 효과 적용 로직 구현
            logger.LogInformation(
                "Player {PlayerId} using consumable item {MsgItemUid} (ItemId={ItemInfoItemId}) on target {MsgTargetItemUid}",
                playerId, msg.ItemUid, itemInfo.ItemId, msg.TargetItemUid);

            await playerInfo.Save(redisOperations);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UseItem 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    // ========== 퀘스트 ==========

    // ========== 메일 ==========

    // ========== 기타 ==========

}
