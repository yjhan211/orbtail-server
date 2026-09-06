namespace user_server.matching.queue;

/// <summary>
///     이번 매치 생성 또는 취소 작업에서 확보한 예약 정보.
///     예약 ID와 플레이어 목록을 보관하고, 매치 번호가 확정되면 함께 기록한다.
///     예약 해제나 롤백 시 다른 작업의 예약을 지우지 않도록 비교 기준으로 사용한다.
/// </summary>
internal sealed class MatchingReservationLease(string reservationId, List<long> playerIds)
{
    public string ReservationId { get; } = reservationId;
    public List<long> PlayerIds { get; } = playerIds;
    public long? MatchingId { get; set; }
}
