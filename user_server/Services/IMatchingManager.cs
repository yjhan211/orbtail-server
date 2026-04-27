using network.common;
using user_server.network;

namespace user_server.services;

public interface IMatchingManager
{
    public Task<ErrorCode> AddToQueue(long playerId, GameSession session);
    public Task<ErrorCode> CancelMatching(long playerId);
    public Task RecordGameCompletionAsync(long playerId);
    public void Dispose();
}
