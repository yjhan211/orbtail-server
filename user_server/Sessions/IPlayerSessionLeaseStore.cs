namespace user_server.sessions;

/// <summary>
///     세션 교체·Redis 조회 실패·응답 지연·해제 실패 상황을
///     실제 Redis 서버 없이 테스트하기 위해 인터페이스로 분리.
/// </summary>
public interface IPlayerSessionLeaseStore
{
    public TimeSpan LeaseLifetime { get; }

    public Task<PlayerSessionLease?> TryAcquireAsync(long playerId, string nodeId, string sessionId);
    public Task<bool> IsCurrentAsync(PlayerSessionLease lease);
    public Task<bool> TryRenewAsync(PlayerSessionLease lease);
    public Task<bool> TryReleaseAsync(PlayerSessionLease lease);
}
