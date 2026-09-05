using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace user_server.sessions;

/// <summary>
///     Redis에 기록되는 UserServer 세션 소유권. Generation이 큰 로그인이 최신이며,
///     OwnerValue는 갱신·해제 시 compare-and-set에 사용하는 변경 불가능한 값이다.
/// </summary>
public sealed record PlayerSessionLease(
    long PlayerId,
    string NodeId,
    string SessionId,
    long Generation,
    string OwnerValue);

/// <summary>
///     플레이어별 현재 UserServer 세션을 획득·갱신·해제하는 전역 lease 저장소.
///     구현은 이전 세션이 최신 세션의 소유권을 갱신하거나 지우지 못하도록 값 비교를 보장한다.
/// </summary>
public interface IPlayerSessionLeaseStore
{
    public TimeSpan LeaseLifetime { get; }
    public TimeSpan RenewalInterval { get; }

    public Task<PlayerSessionLease?> TryAcquireAsync(long playerId, string nodeId, string sessionId);
    public Task<bool> TryRenewAsync(PlayerSessionLease lease);
    public Task<bool> TryReleaseAsync(PlayerSessionLease lease);
}

/// <summary>
///     여러 UserServer 중 어느 세션이 플레이어의 현재 연결인지 Redis에 기록한다.
///     세대 번호는 영속 counter에서 발급하고, owner 교체는 기존 값을 조건으로 갱신해
///     늦게 끝난 로그인과 종료가 더 최신 세션을 덮거나 삭제하지 못하게 한다.
/// </summary>
public sealed class RedisPlayerSessionLeaseStore(
    IRedisOperations redisOperations,
    ILogger<RedisPlayerSessionLeaseStore> logger) : IPlayerSessionLeaseStore
{
    internal const string OwnerKeyPrefix = "user_session_owner:";
    internal const string GenerationKeyPrefix = "user_session_generation:";
    private const int MaxAcquireAttempts = 16;

    public TimeSpan LeaseLifetime { get; } = TimeSpan.FromSeconds(90);
    public TimeSpan RenewalInterval { get; } = TimeSpan.FromSeconds(30);

    public async Task<PlayerSessionLease?> TryAcquireAsync(long playerId, string nodeId, string sessionId)
    {
        ValidateIdentity(playerId, nodeId, sessionId);

        long generation = await redisOperations.StringIncrementAsync(GenerationKey(playerId));
        if (generation <= 0)
            throw new InvalidOperationException($"Invalid session generation {generation} for player {playerId}.");

        string ownerValue = Encode(generation, nodeId, sessionId);
        var lease = new PlayerSessionLease(playerId, nodeId, sessionId, generation, ownerValue);
        string ownerKey = OwnerKey(playerId);

        for (int attempt = 0; attempt < MaxAcquireAttempts; attempt++)
        {
            RedisValue currentValue = await redisOperations.StringGetAsync(ownerKey);
            if (currentValue.IsNullOrEmpty)
            {
                if (await redisOperations.StringSetIfNotExistsAsync(ownerKey, ownerValue, LeaseLifetime))
                    return lease;
                continue;
            }

            string currentOwner = currentValue.ToString();
            if (!TryReadGeneration(currentOwner, out long currentGeneration))
                throw new InvalidDataException($"Malformed player session owner for player {playerId}.");

            // 더 늦게 발급된 로그인이 이미 소유권을 잡았다. 이 로그인은 뒤늦게 완료됐어도 되살리지 않는다.
            if (currentGeneration >= generation)
            {
                logger.LogInformation(
                    "Session lease acquisition superseded: PlayerId={PlayerId}, Generation={Generation}, CurrentGeneration={CurrentGeneration}",
                    playerId, generation, currentGeneration);
                return null;
            }

            if (await redisOperations.StringSetIfEqualsAsync(
                    ownerKey,
                    currentOwner,
                    ownerValue,
                    LeaseLifetime))
                return lease;
        }

        throw new InvalidOperationException(
            $"Player session lease changed too often while acquiring player {playerId}.");
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
        return redisOperations.StringDeleteIfEqualsAsync(
            OwnerKey(lease.PlayerId),
            lease.OwnerValue);
    }

    internal static string OwnerKey(long playerId) => $"{OwnerKeyPrefix}{playerId}";
    internal static string GenerationKey(long playerId) => $"{GenerationKeyPrefix}{playerId}";

    private static string Encode(long generation, string nodeId, string sessionId)
    {
        string encodedNodeId = Convert.ToBase64String(Encoding.UTF8.GetBytes(nodeId));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{generation}|{encodedNodeId}|{sessionId}");
    }

    private static bool TryReadGeneration(string ownerValue, out long generation)
    {
        generation = 0;
        int separator = ownerValue.IndexOf('|');
        return separator > 0 &&
               long.TryParse(
                   ownerValue.AsSpan(0, separator),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out generation) &&
               generation > 0;
    }

    private static void ValidateIdentity(long playerId, string nodeId, string sessionId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    }
}
