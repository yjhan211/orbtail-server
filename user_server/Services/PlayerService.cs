using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     플레이어 관련 모든 로직을 처리하는 서비스
///     - 인벤토리 관리
///     - 퀘스트 관리
///     - 메일 관리
///     - 아이템 사용/착용
/// </summary>
public class PlayerService(ILogger<PlayerService> logger, ICacheHelper cacheHelper, IRedLockFactory redLock)
    : IPlayerService
{
    // ========== 인벤토리 ==========

    public async Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

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

            await playerInfo.Save(cacheHelper);
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
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

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

            await playerInfo.Save(cacheHelper);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UseItem 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    // ========== 퀘스트 ==========

    public async Task<ErrorCode> IncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

            if (playerInfo == null) return ErrorCode.PLAYER_NOT_FOUND;

            // TODO: 퀘스트 카운트 증가 로직
            logger.LogInformation("Player {PlayerId} quest increase: {MsgQuestId}", playerId, msg.QuestId);

            await playerInfo.Save(cacheHelper);
            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IncreaseQuestCount 실패: PlayerId={PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    public async Task<(ErrorCode, PlayerInfo?)> CompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);

            // TODO: 퀘스트 완료 로직
            logger.LogInformation("Player {PlayerId} quest complete: {MsgQuestId}", playerId, msg.QuestId);

            await playerInfo.Save(cacheHelper);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CompleteQuest 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    // ========== 메일 ==========

    public async Task<(ErrorCode, Dictionary<long, MailInfo>?)> GetMailList(long playerId)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);
            return (ErrorCode.SUCCESS, playerInfo.MailBox.MailDict);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetMailList 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    public async Task<(ErrorCode, PlayerInfo?)> ReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);

            // TODO: 메일 수신 로직
            logger.LogInformation("Player {PlayerId} receive mail: {MsgMailUid}", playerId, msg.MailUid);

            await playerInfo.Save(cacheHelper);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReceiveMail 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }

    // ========== 기타 ==========

    public async Task<(ErrorCode, PlayerInfo?)> SetName(long playerId, C_TO_U_SET_NAME msg)
    {
        try
        {
            await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
            var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

            if (playerInfo == null) return (ErrorCode.PLAYER_NOT_FOUND, null);

            playerInfo.Name = msg.Name;
            logger.LogInformation("Player {PlayerId} name changed to: {MsgName}", playerId, msg.Name);

            await playerInfo.Save(cacheHelper);
            return (ErrorCode.SUCCESS, playerInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SetName 실패: PlayerId={PlayerId}", playerId);
            return (ErrorCode.SERVER_INTERNAL_ERROR, null);
        }
    }
}
