using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;

namespace user_server.services;

/// <summary>
/// 플레이어 관련 모든 로직을 처리하는 서비스
/// - 인벤토리 관리
/// - 퀘스트 관리
/// - 메일 관리
/// - 아이템 사용/착용
/// </summary>
public class PlayerService
{
    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;

    public PlayerService(ILogger logger, ICacheHelper cacheHelper, IRedLockFactory redLock)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _redLock = redLock;
    }

    // ========== 인벤토리 ==========

    public async Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return (ErrorCode.FATAL, null);
        }

        // TODO: 착용 로직 구현
        _logger.LogInformation($"Player {playerId} wearing items: {string.Join(", ", msg.ItemUidList)}");

        await playerInfo.Save(_cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    public async Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return (ErrorCode.FATAL, null);
        }

        // TODO: 사용 로직 구현
        _logger.LogInformation($"Player {playerId} using item {msg.ItemUid} on target {msg.TargetItemUid}");

        await playerInfo.Save(_cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 퀘스트 ==========

    public async Task<ErrorCode> IncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return ErrorCode.FATAL;
        }

        // TODO: 퀘스트 카운트 증가 로직
        _logger.LogInformation($"Player {playerId} quest increase: {msg.QuestId}");

        await playerInfo.Save(_cacheHelper);
        return ErrorCode.SUCCESS;
    }

    public async Task<(ErrorCode, PlayerInfo?)> CompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return (ErrorCode.FATAL, null);
        }

        // TODO: 퀘스트 완료 로직
        _logger.LogInformation($"Player {playerId} quest complete: {msg.QuestId}");

        await playerInfo.Save(_cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 메일 ==========

    public async Task<Dictionary<long, MailInfo>?> GetMailList(long playerId)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return null;
        }

        return playerInfo.MailBox.MailDict;
    }

    public async Task<(ErrorCode, PlayerInfo?)> ReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return (ErrorCode.FATAL, null);
        }

        // TODO: 메일 수신 로직
        _logger.LogInformation($"Player {playerId} receive mail: {msg.MailUid}");

        await playerInfo.Save(_cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }

    // ========== 기타 ==========

    public async Task<(ErrorCode, PlayerInfo?)> SetName(long playerId, C_TO_U_SET_NAME msg)
    {
        await using var playerLock = await PlayerInfo.Lock(_redLock, playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);

        if (playerInfo == null)
        {
            return (ErrorCode.FATAL, null);
        }

        playerInfo.Name = msg.Name;
        _logger.LogInformation($"Player {playerId} name changed to: {msg.Name}");

        await playerInfo.Save(_cacheHelper);
        return (ErrorCode.SUCCESS, playerInfo);
    }
}
