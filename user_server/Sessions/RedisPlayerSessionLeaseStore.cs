using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using network.infrastructure.redis;

namespace user_server.sessions;

/// <summary>
///     여러 UserServer가 공유하는 플레이어의 현재 세션 정보를 Redis에 등록·갱신·삭제한다.
///     로그인마다 세대 번호를 발급하며, 더 높은 세대의 세션으로만 교체한다.
///
///     패킷 수신 시 현재 등록된 값과 해당 세션의 값을 비교하고 유효기간을 갱신한다.
///     갱신과 삭제도 값이 일치할 때만 수행해 이전 세션이 새 세션의 등록을 변경하지 못하게 한다.
///
///     등록에는 유효기간을 두어 서버가 비정상 종료돼도 만료 후 정리되도록 한다.
///     PlayerSession이 패킷 수신 시 갱신을 요청하며, 별도의 갱신 타이머는 사용하지 않는다.
/// </summary>
public sealed class RedisPlayerSessionLeaseStore(
    IRedisOperations redisOperations,
    ILogger<RedisPlayerSessionLeaseStore> logger) : IPlayerSessionLeaseStore
{
    private const string OwnerKeyPrefix = "user_session_owner:";
    private const string GenerationKeyPrefix = "user_session_generation:";

    public TimeSpan LeaseLifetime { get; } = TimeSpan.FromSeconds(90);

    public async Task<PlayerSessionLease?> TryAcquireAsync(long playerId, string nodeId, string sessionId)
    {
        ValidateIdentity(playerId, nodeId, sessionId);

        long generation = await redisOperations.StringIncrementAsync(GenerationKey(playerId));
        if (generation <= 0)
            throw new InvalidOperationException($"Invalid session generation {generation} for player {playerId}.");

        string ownerValue = Encode(generation, nodeId, sessionId);
        var lease = new PlayerSessionLease(playerId, nodeId, sessionId, generation, ownerValue);
        bool registered = await redisOperations.StringSetIfNewerGenerationAsync(OwnerKey(playerId), ownerValue, LeaseLifetime);
        if (registered) return lease;

        logger.LogInformation("Session lease rejected by a newer login: PlayerId={PlayerId}, Generation={Generation}",
            playerId, generation);
        return null;
    }

    public Task<bool> TryRenewAsync(PlayerSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return redisOperations.StringSetIfEqualsAsync(
            OwnerKey(lease.PlayerId),
            lease.OwnerValue,
            lease.OwnerValue,
            LeaseLifetime);
    }

    public Task<bool> TryReleaseAsync(PlayerSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return redisOperations.StringDeleteIfEqualsAsync(OwnerKey(lease.PlayerId), lease.OwnerValue);
    }

    internal static string OwnerKey(long playerId) => $"{OwnerKeyPrefix}{playerId}";
    private static string GenerationKey(long playerId) => $"{GenerationKeyPrefix}{playerId}";

    private static string Encode(long generation, string nodeId, string sessionId)
    {
        string encodedNodeId = Convert.ToBase64String(Encoding.UTF8.GetBytes(nodeId));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{generation}|{encodedNodeId}|{sessionId}");
    }

    private static void ValidateIdentity(long playerId, string nodeId, string sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(playerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    }
}
