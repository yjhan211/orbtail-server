using network.common;
using user_server.network;

namespace user_server.services;

public interface IMatchingManager
{
    public Task<ErrorCode> AddToQueue(long playerId, GameSession session);
    public Task<ErrorCode> CancelMatching(long playerId);
    public Task RecordGameCompletionAsync(long playerId, long matchingId);
    public Task RecordLeaveAsync(long playerId, long matchingId);
    public Task AbortMatchingAdmissionAsync(long playerId, long matchingId);
    public Task ReleaseMatchingClaimAsync(long playerId, long matchingId);
    public bool TryRunBackgroundOperation(Func<Task> operation, string operationName);
    public Task QuiesceAsync();
    public Task StopAsync();
}
