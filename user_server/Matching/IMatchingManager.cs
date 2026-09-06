using network.common;
using user_server.sessions;

namespace user_server.matching;

/// <summary>
///     PlayerSession을 실제 매칭 시스템 없이 테스트할 수 있도록
///     매칭 요청·취소·배정 정리 등의 기능을 인터페이스로 분리.
/// </summary>
public interface IMatchingManager
{
    public Task<ErrorCode> AddToQueue(long playerId, PlayerSession session);
    public Task<ErrorCode> CancelMatching(long playerId);
    public Task<bool> HasReservationAsync(long playerId);
    public Task HandleEntryFailureAsync(long playerId, long matchingId);
    public Task ReleaseMatchingReservationAsync(long playerId, long matchingId);
    public Task StopMatchingLoopAsync();
    public Task StopAsync();
}
