using System.Globalization;
using MessagePack;
using network.common;
using network.contracts.messaging;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure;

public sealed class MatchingLifecycleOutboxStore(IRedisConnectionPool redisPool)
{
    public static readonly TimeSpan DefaultRecordLifetime = TimeSpan.FromDays(8);
    public static readonly TimeSpan MinimumRecordLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultPublishedTombstoneLifetime = TimeSpan.FromDays(8);

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private const string EnqueueScript = """
        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        local staleBefore = now - tonumber(ARGV[3])
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', staleBefore)

        if redis.call('EXISTS', KEYS[3]) ~= 0 then
            return 2
        end

        local existing = redis.call('GET', KEYS[1])
        if existing then
            if existing ~= ARGV[1] then
                return -1
            end
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
            redis.call('ZADD', KEYS[2], 'NX', now, ARGV[2])
            return 0
        end

        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[3])
        redis.call('ZADD', KEYS[2], now, ARGV[2])
        return 1
        """;

    private const string AcquireAbortFenceScript = """
        for index = 2, #KEYS do
            if redis.call('EXISTS', KEYS[index]) ~= 0 then
                return 0
            end
        end

        local existing = redis.call('GET', KEYS[1])
        if existing then
            if existing ~= ARGV[1] then
                return -1
            end
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            return 1
        end

        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        return 1
        """;

    private const string ClaimDueScript = """
        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        local staleBefore = now - tonumber(ARGV[3])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', staleBefore)

        local fingerprints = redis.call(
            'ZRANGEBYSCORE', KEYS[1], '-inf', now, 'LIMIT', 0, tonumber(ARGV[1]))
        local leaseUntil = now + tonumber(ARGV[2])
        local result = { tostring(leaseUntil) }
        for _, fingerprint in ipairs(fingerprints) do
            redis.call('ZADD', KEYS[1], 'XX', leaseUntil, fingerprint)
            table.insert(result, fingerprint)
        end
        return result
        """;

    private const string CompleteScript = """
        local score = redis.call('ZSCORE', KEYS[2], ARGV[2])
        if not score then
            if redis.call('EXISTS', KEYS[1]) == 0 then
                redis.call('SET', KEYS[3], '1', 'PX', ARGV[4])
                return 2
            end
            return 0
        end
        if tonumber(score) ~= tonumber(ARGV[3]) then
            return 0
        end

        local existing = redis.call('GET', KEYS[1])
        if not existing then
            redis.call('ZREM', KEYS[2], ARGV[2])
            redis.call('SET', KEYS[3], '1', 'PX', ARGV[4])
            return 2
        end
        if existing ~= ARGV[1] then
            return -1
        end

        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[2])
        redis.call('SET', KEYS[3], '1', 'PX', ARGV[4])
        return 1
        """;

    private const string GetPublicationStateScript = """
        if redis.call('EXISTS', KEYS[1]) ~= 0 then
            return 1
        end
        if redis.call('EXISTS', KEYS[2]) ~= 0 then
            return 2
        end
        return 0
        """;

    private const string RemoveMissingScript = """
        if redis.call('EXISTS', KEYS[1]) ~= 0 then
            return 0
        end
        local score = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not score or tonumber(score) ~= tonumber(ARGV[2]) then
            return 0
        end
        redis.call('ZREM', KEYS[2], ARGV[1])
        return 1
        """;

    private const string RescheduleScript = """
        local score = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not score or tonumber(score) ~= tonumber(ARGV[2]) then
            return 0
        end
        if redis.call('EXISTS', KEYS[1]) == 0 then
            redis.call('ZREM', KEYS[2], ARGV[1])
            return 2
        end

        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        redis.call('ZADD', KEYS[2], 'XX', now + tonumber(ARGV[3]), ARGV[1])
        return 1
        """;

    public async Task<MatchingLifecycleOutboxEnqueueResult> EnqueueAsync(
        MatchingLifecycleOutboxRecord record,
        TimeSpan? recordLifetime = null)
    {
        ValidateRecord(record);
        TimeSpan lifetime = recordLifetime ?? DefaultRecordLifetime;
        long lifetimeMilliseconds = ValidateRecordLifetime(lifetime);
        byte[] serialized = MessagePackSerializer.Serialize(record, SerializerOptions);

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                EnqueueScript,
                [
                    MatchingLifecycleOutboxKeys.Record(record.EventIdFingerprint),
                    MatchingLifecycleOutboxKeys.Due,
                    MatchingLifecycleOutboxKeys.AbortFence(record.PlayerId, record.MatchingId)
                ],
                [serialized, record.EventIdFingerprint, lifetimeMilliseconds],
                CommandFlags.DemandMaster));

        return (long)result switch
        {
            1 => MatchingLifecycleOutboxEnqueueResult.Added,
            0 => MatchingLifecycleOutboxEnqueueResult.AlreadyPresent,
            2 => MatchingLifecycleOutboxEnqueueResult.Fenced,
            -1 => throw new InvalidOperationException(
                $"Matching lifecycle outbox event '{record.EventId}' already has different payload."),
            long status => throw new InvalidOperationException(
                $"Redis returned invalid lifecycle outbox enqueue status {status}.")
        };
    }

    public async Task<MatchingLifecycleAbortFenceAcquireResult> TryAcquireAbortFenceAsync(
        long playerId,
        long matchingId,
        TimeSpan? fenceLifetime = null)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        long lifetimeMilliseconds = ValidateRecordLifetime(fenceLifetime ?? DefaultRecordLifetime);
        string[] subjects =
        [
            MatchingLifecycleSubjects.PlayerCompleted,
            MatchingLifecycleSubjects.PlayerReleased,
            MatchingLifecycleSubjects.PlayerLeft,
            MatchingLifecycleSubjects.PlayerAdmissionFailed
        ];
        var keys = new RedisKey[1 + subjects.Length * 2];
        keys[0] = MatchingLifecycleOutboxKeys.AbortFence(playerId, matchingId);
        for (int index = 0; index < subjects.Length; index++)
        {
            string eventId = MatchingLifecycleMessageIds.Create(subjects[index], playerId, matchingId);
            string fingerprint = MatchingLifecycleOutboxKeys.FingerprintEventId(eventId);
            keys[1 + index * 2] = MatchingLifecycleOutboxKeys.Record(fingerprint);
            keys[2 + index * 2] = MatchingLifecycleOutboxKeys.Published(fingerprint);
        }

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                AcquireAbortFenceScript,
                keys,
                ["abort-v1", lifetimeMilliseconds],
                CommandFlags.DemandMaster));
        return (long)result switch
        {
            0 => MatchingLifecycleAbortFenceAcquireResult.Protected,
            1 => MatchingLifecycleAbortFenceAcquireResult.Acquired,
            -1 => throw new InvalidOperationException(
                "Redis contains a conflicting matching lifecycle abort fence."),
            long status => throw new InvalidOperationException(
                $"Redis returned invalid lifecycle abort-fence status {status}.")
        };
    }

    public async Task<IReadOnlyList<MatchingLifecycleOutboxClaim>> ClaimDueAsync(
        int maximumCount,
        TimeSpan claimLease,
        TimeSpan? recordLifetime = null)
    {
        if (maximumCount is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        long claimLeaseMilliseconds = ValidatePositiveMilliseconds(claimLease, nameof(claimLease));
        long recordLifetimeMilliseconds = ValidateRecordLifetime(recordLifetime ?? DefaultRecordLifetime);

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                ClaimDueScript,
                [MatchingLifecycleOutboxKeys.Due],
                [maximumCount, claimLeaseMilliseconds, recordLifetimeMilliseconds],
                CommandFlags.DemandMaster));
        RedisResult[] values = (RedisResult[])result!;
        if (values.Length == 0 ||
            !long.TryParse(
                values[0].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long leaseUntilUnixMilliseconds) ||
            leaseUntilUnixMilliseconds <= 0)
        {
            throw new InvalidOperationException("Redis returned an invalid lifecycle outbox claim lease.");
        }

        if (values.Length == 1)
            return [];

        string[] fingerprints = new string[values.Length - 1];
        RedisKey[] recordKeys = new RedisKey[fingerprints.Length];
        for (int index = 1; index < values.Length; index++)
        {
            string fingerprint = values[index].ToString();
            if (!MatchingLifecycleOutboxKeys.IsValidFingerprint(fingerprint))
                throw new InvalidOperationException("Redis returned an invalid lifecycle outbox fingerprint.");
            fingerprints[index - 1] = fingerprint;
            recordKeys[index - 1] = MatchingLifecycleOutboxKeys.Record(fingerprint);
        }

        RedisValue[] records = await redisPool.ExecuteWithRetryAsync(
            database => database.StringGetAsync(recordKeys, CommandFlags.DemandMaster));
        var claims = new List<MatchingLifecycleOutboxClaim>(fingerprints.Length);
        for (int index = 0; index < fingerprints.Length; index++)
        {
            byte[]? rawRecord = records[index].IsNull ? null : (byte[])records[index]!;
            MatchingLifecycleOutboxRecord? record = TryDeserializeRecord(rawRecord);
            claims.Add(new MatchingLifecycleOutboxClaim(
                fingerprints[index],
                leaseUntilUnixMilliseconds,
                rawRecord,
                record));
        }
        return claims;
    }

    public async Task<bool> CompleteAsync(
        MatchingLifecycleOutboxClaim claim,
        TimeSpan? publishedTombstoneLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.RawRecord == null)
            throw new ArgumentException("A persisted outbox record is required.", nameof(claim));
        ValidateClaimIdentity(claim);
        long tombstoneLifetimeMilliseconds = ValidatePositiveMilliseconds(
            publishedTombstoneLifetime ?? DefaultPublishedTombstoneLifetime,
            nameof(publishedTombstoneLifetime));

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                CompleteScript,
                [
                    MatchingLifecycleOutboxKeys.Record(claim.EventIdFingerprint),
                    MatchingLifecycleOutboxKeys.Due,
                    MatchingLifecycleOutboxKeys.Published(claim.EventIdFingerprint)
                ],
                [
                    claim.RawRecord,
                    claim.EventIdFingerprint,
                    claim.LeaseUntilUnixMilliseconds,
                    tombstoneLifetimeMilliseconds
                ],
                CommandFlags.DemandMaster));
        return (long)result switch
        {
            1 or 2 => true,
            0 => false,
            -1 => throw new InvalidOperationException(
                $"Lifecycle outbox record changed while completing {claim.EventIdFingerprint}."),
            long status => throw new InvalidOperationException(
                $"Redis returned invalid lifecycle outbox completion status {status}.")
        };
    }

    public async Task<MatchingLifecycleOutboxPublicationState> GetPublicationStateAsync(
        string subject,
        long playerId,
        long matchingId)
    {
        string eventId = MatchingLifecycleMessageIds.Create(subject, playerId, matchingId);
        string fingerprint = MatchingLifecycleOutboxKeys.FingerprintEventId(eventId);
        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                GetPublicationStateScript,
                [
                    MatchingLifecycleOutboxKeys.Record(fingerprint),
                    MatchingLifecycleOutboxKeys.Published(fingerprint)
                ],
                [],
                CommandFlags.DemandMaster));
        return (long)result switch
        {
            0 => MatchingLifecycleOutboxPublicationState.None,
            1 => MatchingLifecycleOutboxPublicationState.Pending,
            2 => MatchingLifecycleOutboxPublicationState.RecentlyPublished,
            long status => throw new InvalidOperationException(
                $"Redis returned invalid lifecycle outbox publication state {status}.")
        };
    }

    public async Task<bool> HasPendingOrRecentlyPublishedAsync(
        string subject,
        long playerId,
        long matchingId)
    {
        return await GetPublicationStateAsync(subject, playerId, matchingId) !=
               MatchingLifecycleOutboxPublicationState.None;
    }

    public async Task<bool> RemoveMissingAsync(MatchingLifecycleOutboxClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateClaimIdentity(claim);

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                RemoveMissingScript,
                [
                    MatchingLifecycleOutboxKeys.Record(claim.EventIdFingerprint),
                    MatchingLifecycleOutboxKeys.Due
                ],
                [claim.EventIdFingerprint, claim.LeaseUntilUnixMilliseconds],
                CommandFlags.DemandMaster));
        return (long)result == 1;
    }

    public async Task<bool> RescheduleAsync(
        MatchingLifecycleOutboxClaim claim,
        TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateClaimIdentity(claim);
        long retryDelayMilliseconds = ValidatePositiveMilliseconds(retryDelay, nameof(retryDelay));

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                RescheduleScript,
                [
                    MatchingLifecycleOutboxKeys.Record(claim.EventIdFingerprint),
                    MatchingLifecycleOutboxKeys.Due
                ],
                [
                    claim.EventIdFingerprint,
                    claim.LeaseUntilUnixMilliseconds,
                    retryDelayMilliseconds
                ],
                CommandFlags.DemandMaster));
        return (long)result is 1 or 2;
    }

    private static MatchingLifecycleOutboxRecord? TryDeserializeRecord(byte[]? rawRecord)
    {
        if (rawRecord == null)
            return null;
        try
        {
            MatchingLifecycleOutboxRecord? record =
                MessagePackSerializer.Deserialize<MatchingLifecycleOutboxRecord>(rawRecord, SerializerOptions);
            return record is { IsValid: true } ? record : null;
        }
        catch (MessagePackSerializationException)
        {
            return null;
        }
    }

    private static void ValidateRecord(MatchingLifecycleOutboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsValid)
            throw new ArgumentException("Matching lifecycle outbox record is invalid.", nameof(record));
    }

    private static void ValidateClaimIdentity(MatchingLifecycleOutboxClaim claim)
    {
        if (!MatchingLifecycleOutboxKeys.IsValidFingerprint(claim.EventIdFingerprint) ||
            claim.LeaseUntilUnixMilliseconds <= 0)
        {
            throw new ArgumentException("Matching lifecycle outbox claim is invalid.", nameof(claim));
        }
    }

    private static long ValidateRecordLifetime(TimeSpan lifetime)
    {
        if (lifetime < MinimumRecordLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Lifecycle outbox records must live for at least {MinimumRecordLifetime.TotalDays} days.");
        }
        return ValidatePositiveMilliseconds(lifetime, nameof(lifetime));
    }

    private static long ValidatePositiveMilliseconds(TimeSpan value, string parameterName)
    {
        double milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (!double.IsFinite(milliseconds) || milliseconds <= 0 || milliseconds > long.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName);
        return (long)milliseconds;
    }
}

public enum MatchingLifecycleOutboxEnqueueResult
{
    Added,
    AlreadyPresent,
    Fenced
}

public enum MatchingLifecycleAbortFenceAcquireResult
{
    Protected,
    Acquired
}

public enum MatchingLifecycleOutboxPublicationState
{
    None,
    Pending,
    RecentlyPublished
}

public sealed record MatchingLifecycleOutboxClaim(
    string EventIdFingerprint,
    long LeaseUntilUnixMilliseconds,
    byte[]? RawRecord,
    MatchingLifecycleOutboxRecord? Record);
