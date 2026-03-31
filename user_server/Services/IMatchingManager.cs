using network.common;
using user_server.network;

namespace user_server.services;

public interface IMatchingManager
{
    Task<ErrorCode> AddToQueue(long playerId, GameSession session);
    Task<ErrorCode> CancelMatching(long playerId);
    void Dispose();
}
