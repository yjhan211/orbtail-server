using network.common;
using network.common.data.models;

namespace user_server.services;

public interface IPlayerService
{
    public Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg);
    public Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg);
    public Task<ErrorCode> IncreaseQuestCount(long playerId, C_TO_U_QUEST_INCREASE msg);
    public Task<(ErrorCode, PlayerInfo?)> CompleteQuest(long playerId, C_TO_U_QUEST_SUCCESS msg);
    public Task<(ErrorCode, Dictionary<long, MailInfo>?)> GetMailList(long playerId);
    public Task<(ErrorCode, PlayerInfo?)> ReceiveMail(long playerId, C_TO_U_MAIL_RECEIVE msg);
    public Task<(ErrorCode, PlayerInfo?)> SetName(long playerId, C_TO_U_SET_NAME msg);
}
