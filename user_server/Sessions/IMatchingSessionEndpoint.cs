using network.packets;

namespace user_server.sessions;

/// <summary>
///     라우터가 세션에 요청할 수 있는 매칭 결과 전달·배정 해제·이전 로그인 종료 동작을 정의한다.
///     PlayerSession이 구현하며, 라우터 테스트에서는 실제 TCP 연결 없이 가짜 세션으로 대신한다.
/// </summary>
internal interface IMatchingSessionEndpoint
{
    public bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet);
    public bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet);
    public bool TryDeliverEntryFailed(long matchingId, Packet packet);
    public void ClearMatchingAssignment(long matchingId);
    public void DisconnectIfOlderSession(long newGeneration);
}
