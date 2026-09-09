
namespace game_server.matches;

/// <summary>
///     세션의 퇴장·게임 완료 알림과 매칭 예약 해제를 요청하는 인터페이스.
///     세션 테스트에서 실제 Redis와 NATS 없이 정리 요청을 검증하기 위해 분리.
/// </summary>
internal interface IMatchSessionCleanup
{
    void PublishPlayerLeft(long playerId, long matchingId);
    Action? PrepareGameCompletion(long playerId, long matchingId);
    void ReleaseMatchingReservation(long playerId, long matchingId);
}
