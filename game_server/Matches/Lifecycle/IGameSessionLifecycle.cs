
namespace game_server.matches.lifecycle;

/// <summary>
///     게임 세션의 퇴장·완료·예약 해제를 알리는 경계.
///     세션 테스트에서 실제 Redis와 NATS 없이 발행 시점과 실패를 검증한다.
/// </summary>
internal interface IGameSessionLifecycle
{
    void PublishPlayerLeft(long playerId, long matchingId);
    Action? PrepareGameCompletion(long playerId, long matchingId);
    void ReleaseMatchingReservation(long playerId, long matchingId);
}
