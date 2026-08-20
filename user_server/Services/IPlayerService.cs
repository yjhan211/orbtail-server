using network.common;
using network.common.data.models;

namespace user_server.services;

public interface IPlayerService
{
    public Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg);
    public Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg);
}
