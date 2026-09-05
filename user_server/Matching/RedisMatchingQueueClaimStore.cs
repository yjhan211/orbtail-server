using network.common;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace user_server.matching;

/// <summary>
///     Performs matching-queue claim acquisition as one Redis-side state transition.
/// </summary>
public sealed class RedisMatchingQueueClaimStore(RedisConnection redisConnection) : IMatchingQueueClaimStore
{
    private static readonly TimeSpan MinimumRedisLifetime = TimeSpan.FromMilliseconds(1);

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
        if (expiry < MinimumRedisLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiry),
                $"Expiry must be at least {MinimumRedisLifetime.TotalMilliseconds:0} millisecond.");
        }

        var result = await redisConnection.GetDatabase().ScriptEvaluateAsync(
            TryClaimQueueEntryScript,
            [MatchingHandoffRedisKeys.MatchingQueueKey, MatchingHandoffRedisKeys.ClaimKey(playerId)],
            [queueEntry, claimId, checked((long)expiry.TotalMilliseconds)],
            CommandFlags.DemandMaster);
        return (long)result == 1;
    }
}
