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
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return (ErrorCode.FATAL, null);

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
                logger.LogWarning($"Player {playerId} tried to wear non-existent item UID: {itemUid}");
            }

        logger.LogInformation($"Player {playerId} wearing items: {string.Join(", ", msg.ItemUidList)}");

        await playerInfo.Save(cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    public async Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return (ErrorCode.FATAL, null);

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
                $"Player {playerId} tried to use non-consumable item: ItemId={itemInfo.ItemId}, Type={itemType}");
            return (ErrorCode.INVALID_ITEM_TYPE, playerInfo);
        }

        // TODO: 소비 아이템 효과 적용 로직 구현
        logger.LogInformation(
            $"Player {playerId} using consumable item {msg.ItemUid} (ItemId={itemInfo.ItemId}) on target {msg.TargetItemUid}");

        await playerInfo.Save(cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 퀘스트 ==========

    public async Task<ErrorCode> IncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE msg)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return ErrorCode.FATAL;

        // TODO: 퀘스트 카운트 증가 로직
        logger.LogInformation($"Player {playerId} quest increase: {msg.QuestId}");

        await playerInfo.Save(cacheHelper);
        return ErrorCode.SUCCESS;
    }

    public async Task<(ErrorCode, PlayerInfo?)> CompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS msg)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return (ErrorCode.FATAL, null);

        // TODO: 퀘스트 완료 로직
        logger.LogInformation($"Player {playerId} quest complete: {msg.QuestId}");

        await playerInfo.Save(cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 메일 ==========

    public async Task<Dictionary<long, MailInfo>?> GetMailList(long playerId)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return null;

        return playerInfo.MailBox.MailDict;
    }

    public async Task<(ErrorCode, PlayerInfo?)> ReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE msg)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return (ErrorCode.FATAL, null);

        // TODO: 메일 수신 로직
        logger.LogInformation($"Player {playerId} receive mail: {msg.MailUid}");

        await playerInfo.Save(cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 기타 ==========

    public async Task<(ErrorCode, PlayerInfo?)> SetName(long playerId, C_TO_U_SET_NAME msg)
    {
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(cacheHelper, playerId);

        if (playerInfo == null) return (ErrorCode.FATAL, null);

        playerInfo.Name = msg.Name;
        logger.LogInformation("Player {PlayerId} name changed to: {MsgName}", playerId, msg.Name);

        await playerInfo.Save(cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }
}
