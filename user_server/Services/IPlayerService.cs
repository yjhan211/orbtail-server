using network.common;
using network.common.data.models;

namespace user_server.services;

public interface IPlayerService
{
    Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg);
    Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg);
    Task<ErrorCode> IncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE msg);
    Task<(ErrorCode, PlayerInfo?)> CompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS msg);
    Task<Dictionary<long, MailInfo>?> GetMailList(long playerId);
    Task<(ErrorCode, PlayerInfo?)> ReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE msg);
    Task<(ErrorCode, PlayerInfo?)> SetName(long playerId, C_TO_U_SET_NAME msg);
}
