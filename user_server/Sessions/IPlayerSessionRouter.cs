using network.common;
using network.common.data.models;

namespace user_server.sessions;

/// <summary>
///     플레이어의 접속 서버를 몰라도 매칭 결과와 세션 알림을 전달할 수 있게 한다.
///     매칭 로직을 실제 NATS 통신 없이 테스트할 수 있도록 인터페이스로 분리.
/// </summary>
internal interface IPlayerSessionRouter
{
    public Task<bool> DeliverMatchingSuccessAsync(long playerId, string requestId, U_TO_C_MATCHING_SUCCESS result);
    public Task<bool> DeliverMatchingFailedAsync(long playerId, long matchingId, string requestId, ErrorCode errorCode);
    public Task<bool> DeliverAdmissionFailedAsync(long playerId, long matchingId, ErrorCode errorCode);
    public void ClearMatchingAssignment(long playerId, long matchingId);
    public void AnnounceLogin(long playerId, long generation);
}
