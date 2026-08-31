using network.common;
using network.interfaces;
using StackExchange.Redis;

namespace user_server.services.scaling;

/// <summary>
///     Performs matching-queue claim acquisition as one Redis-side state transition.
/// </summary>
public sealed class RedisMatchingQueueClaimStore(IRedisConnectionPool redisPool) : IMatchingQueueClaimStore
{
    private const string MatchingQueueKey = "matching_queue";
    private const string TryClaimQueueEntryScript = """
        if redis.call('ZSCORE', KEYS[1], ARGV[1]) == false then
            return 0
        end
        if redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[3], 'NX') then
            return 1
        end
        return 0
        """;

    public async Task<bool> TryClaimQueueEntryAsync(
        byte[] queueEntry,
        long playerId,
        string claimId,
        TimeSpan expiry)
    {
        ArgumentNullException.ThrowIfNull(queueEntry);
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be greater than zero.");

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                TryClaimQueueEntryScript,
                [MatchingQueueKey, MatchingHandoffRedisKeys.ClaimKey(playerId)],
                [queueEntry, claimId, checked((long)expiry.TotalMilliseconds)],
                CommandFlags.DemandMaster),
            retryCount: 1);
        return (long)result == 1;
    }
}
