using System.Globalization;
using MessagePack;
using network.common;
using network.interfaces;
using StackExchange.Redis;

namespace user_server.services.scaling;

public sealed class RedisUserServerCoordinationStore(IRedisConnectionPool redisPool)
    : IUserServerCoordinationStore
{
    private static readonly MessagePackSerializerOptions RecoverySerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private const string AcquireNodeLeaseScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
            return 1
        end
        if current == ARGV[1] then
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            return 1
        end
        return 0
        """;

    private const string RenewNodeLeaseScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    private const string ReleaseIfEqualsScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    private const string AcquireSessionOwnerScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return {-1, '', '', 0}
        end

        local receipt = redis.call('GET', KEYS[4])
        if receipt then
            local separator = string.find(receipt, '\n', 1, true)
            if not separator then
                return {-2, '', '', 0}
            end

            local receiptOwner = string.sub(receipt, 1, separator - 1)
            local receiptPrevious = string.sub(receipt, separator + 1)
            if redis.call('GET', KEYS[2]) ~= receiptOwner then
                return {0, receiptOwner, receiptPrevious, 1}
            end

            redis.call('PEXPIRE', KEYS[2], ARGV[4])
            redis.call('PEXPIRE', KEYS[4], ARGV[4])
            return {1, receiptOwner, receiptPrevious, 1}
        end

        local prefix = ARGV[2] .. '|' .. ARGV[1] .. '|' .. ARGV[3] .. '|'
        local current = redis.call('GET', KEYS[2])
        if current and string.sub(current, 1, string.len(prefix)) == prefix then
            redis.call('PEXPIRE', KEYS[2], ARGV[4])
            redis.call('SET', KEYS[4], current .. '\n', 'PX', ARGV[4])
            return {1, current, '', 1}
        end

        local previous = current or ''
        local epoch = redis.call('INCR', KEYS[3])
        local owner = prefix .. tostring(epoch)
        redis.call('SET', KEYS[2], owner, 'PX', ARGV[4])
        redis.call('SET', KEYS[4], owner .. '\n' .. previous, 'PX', ARGV[4])
        return {1, owner, previous, 0}
        """;

    private const string RenewSessionOwnerScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        if redis.call('GET', KEYS[2]) ~= ARGV[2] then
            return 0
        end
        redis.call('PEXPIRE', KEYS[2], ARGV[3])
        return 1
        """;

    private const string ReleaseSessionOwnerScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        redis.call('DEL', KEYS[1])
        redis.call('DEL', KEYS[2])
        return 1
        """;

    private const string RestorePreviousSessionOwnerScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end

        if ARGV[3] ~= '' and redis.call('GET', KEYS[3]) == ARGV[2] then
            redis.call('SET', KEYS[1], ARGV[3], 'PX', ARGV[4])
        else
            redis.call('DEL', KEYS[1])
        end
        redis.call('DEL', KEYS[2])
        return 1
        """;

    private const string AcquireMatchingLeaderScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return {-1, ''}
        end

        local prefix = ARGV[2] .. '|' .. ARGV[1] .. '|'
        local current = redis.call('GET', KEYS[2])
        if current then
            if string.sub(current, 1, string.len(prefix)) == prefix then
                redis.call('PEXPIRE', KEYS[2], ARGV[3])
                return {1, current}
            end
            return {0, current}
        end

        local fence = redis.call('INCR', KEYS[3])
        local leader = prefix .. tostring(fence)
        redis.call('SET', KEYS[2], leader, 'PX', ARGV[3])
        return {1, leader}
        """;

    private const string RenewMatchingLeaderScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        if redis.call('GET', KEYS[2]) ~= ARGV[2] then
            return 0
        end
        redis.call('PEXPIRE', KEYS[2], ARGV[3])
        return 1
        """;

    private const string RegisterMatchingAdmissionRecoveryScript = """
        if ARGV[1] ~= '' then
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then
                return -1
            end
            if redis.call('GET', KEYS[2]) ~= ARGV[2] then
                return -1
            end
        end

        local existingState = redis.call('GET', KEYS[3])
        if existingState and existingState ~= ARGV[4] then
            return -3
        end

        local existingRecord = redis.call('GET', KEYS[4])
        if existingRecord and existingRecord ~= ARGV[3] then
            return -2
        end

        redis.call('SET', KEYS[3], ARGV[4], 'PX', ARGV[6])
        redis.call('SET', KEYS[4], ARGV[3], 'PX', ARGV[6])
        redis.call('ZADD', KEYS[5], ARGV[5], ARGV[7])

        local indexTtl = redis.call('PTTL', KEYS[5])
        if indexTtl < tonumber(ARGV[6]) then
            redis.call('PEXPIRE', KEYS[5], ARGV[6])
        end
        return 1
        """;

    private const string RemoveMatchingAdmissionRecoveryScript = """
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        return 1
        """;

    private const string UpdateMatchingAdmissionRecoveryScript = """
        if ARGV[1] ~= '' then
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then
                return -1
            end
            if redis.call('GET', KEYS[2]) ~= ARGV[2] then
                return -1
            end
        end
        if redis.call('GET', KEYS[3]) ~= ARGV[3] then
            return 0
        end

        redis.call('SET', KEYS[3], ARGV[4], 'PX', ARGV[6])
        redis.call('ZADD', KEYS[4], ARGV[5], ARGV[7])
        local indexTtl = redis.call('PTTL', KEYS[4])
        if indexTtl < tonumber(ARGV[6]) then
            redis.call('PEXPIRE', KEYS[4], ARGV[6])
        end
        return 1
        """;

    private const string GetRedisTimeScript = "return redis.call('TIME')";

    private const string ApplyMatchingLifecycleOnceScript = """
        local existingMarker = redis.call('GET', KEYS[1])
        if existingMarker then
            redis.call('PEXPIRE', KEYS[1], ARGV[6])
            return {0, 0, existingMarker}
        end

        local function decodePositiveInt64(value)
            if not value then
                return nil
            end
            if string.len(value) ~= 8 or string.byte(value, 8) >= 128 then
                return false
            end

            local result = 0
            local multiplier = 1
            for index = 1, 8 do
                result = result + string.byte(value, index) * multiplier
                multiplier = multiplier * 256
            end
            return result
        end

        local function encodePositiveInt64(value)
            local bytes = {}
            for index = 1, 8 do
                local nextByte = value % 256
                bytes[index] = nextByte
                value = (value - nextByte) / 256
            end
            return string.char(unpack(bytes))
        end

        if ARGV[3] == '1' or ARGV[3] == '2' then
            local penalty = redis.call('HGET', KEYS[3], ARGV[2])
            local penaltyCount = decodePositiveInt64(penalty)
            if penaltyCount == false then
                return {-2, 0, ''}
            end

            if ARGV[3] == '1' then
                penaltyCount = (penaltyCount or 0) + 1
                redis.call('HSET', KEYS[3], ARGV[2], encodePositiveInt64(penaltyCount))
                if not penalty then
                    redis.call('HSET', KEYS[4], ARGV[2], encodePositiveInt64(tonumber(ARGV[5])))
                end
            elseif penaltyCount then
                if penaltyCount <= 1 then
                    redis.call('HDEL', KEYS[3], ARGV[2])
                    redis.call('HDEL', KEYS[4], ARGV[2])
                else
                    redis.call('HSET', KEYS[3], ARGV[2], encodePositiveInt64(penaltyCount - 1))
                end
            end
        end

        local claimReleased = 0
        if redis.call('GET', KEYS[2]) == ARGV[1] then
            redis.call('DEL', KEYS[2])
            claimReleased = 1
        end

        redis.call('SET', KEYS[1], ARGV[4], 'PX', ARGV[6])
        return {1, claimReleased, ARGV[4]}
        """;

    public async Task<bool> TryAcquireNodeLeaseAsync(UserServerProcessIdentity identity, TimeSpan lifetime)
    {
        ValidateIdentity(identity);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            AcquireNodeLeaseScript,
            [UserServerScalingKeys.NodeLease(identity.NodeId)],
            [identity.Generation, lifetimeMilliseconds]);
        return (long)result == 1;
    }

    public async Task<bool> RenewNodeLeaseAsync(UserServerProcessIdentity identity, TimeSpan lifetime)
    {
        ValidateIdentity(identity);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            RenewNodeLeaseScript,
            [UserServerScalingKeys.NodeLease(identity.NodeId)],
            [identity.Generation, lifetimeMilliseconds]);
        return (long)result == 1;
    }

    public async Task<bool> ReleaseNodeLeaseAsync(UserServerProcessIdentity identity)
    {
        ValidateIdentity(identity);

        RedisResult result = await EvaluateAsync(
            ReleaseIfEqualsScript,
            [UserServerScalingKeys.NodeLease(identity.NodeId)],
            [identity.Generation]);
        return (long)result == 1;
    }

    public async Task<UserSessionOwnerAcquisition?> TryAcquireOrReplaceSessionOwnerAsync(
        UserServerProcessIdentity identity,
        long playerId,
        string sessionId,
        TimeSpan lifetime)
    {
        ValidateIdentity(identity);
        ValidatePlayerId(playerId);
        ValidateTokenComponent(sessionId, nameof(sessionId));
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            AcquireSessionOwnerScript,
            [
                UserServerScalingKeys.NodeLease(identity.NodeId),
                UserServerScalingKeys.SessionOwner(playerId),
                UserServerScalingKeys.SessionEpoch(playerId),
                UserServerScalingKeys.SessionAcquireReceipt(playerId, sessionId)
            ],
            [identity.Generation, identity.NodeId, sessionId, lifetimeMilliseconds]);

        RedisResult[] values = RequireArray(result, 4, "session owner acquisition");
        long status = (long)values[0];
        if (status == -2)
            throw new InvalidOperationException("Redis contains an invalid session acquisition receipt.");
        if (status != 1)
            return null;

        string ownerToken = RequireString(values[1], "session owner token");
        if (!UserServerScalingKeys.TryParseSessionOwnerToken(playerId, ownerToken, out UserSessionOwner? owner) ||
            owner == null)
        {
            throw new InvalidOperationException("Redis returned an invalid session owner token.");
        }

        string previousToken = values[2].IsNull ? string.Empty : values[2].ToString();
        UserSessionOwner? previousOwner = null;
        if (!string.IsNullOrEmpty(previousToken) &&
            !UserServerScalingKeys.TryParseSessionOwnerToken(playerId, previousToken, out previousOwner))
        {
            throw new InvalidOperationException("Redis returned an invalid previous session owner token.");
        }

        bool wasAlreadyOwned = (long)values[3] == 1;
        if (wasAlreadyOwned && string.Equals(previousToken, ownerToken, StringComparison.Ordinal))
            previousOwner = null;

        return new UserSessionOwnerAcquisition(owner, previousOwner, wasAlreadyOwned);
    }

    public async Task<bool> RenewSessionOwnerAsync(UserSessionOwner owner, TimeSpan lifetime)
    {
        ValidateSessionOwner(owner);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            RenewSessionOwnerScript,
            [
                UserServerScalingKeys.NodeLease(owner.NodeId),
                UserServerScalingKeys.SessionOwner(owner.PlayerId)
            ],
            [owner.NodeGeneration, owner.Token, lifetimeMilliseconds]);
        return (long)result == 1;
    }

    public async Task<UserSessionOwner?> GetSessionOwnerAsync(long playerId)
    {
        ValidatePlayerId(playerId);

        RedisValue value = await redisPool.ExecuteWithRetryAsync(
            database => database.StringGetAsync(
                UserServerScalingKeys.SessionOwner(playerId),
                CommandFlags.DemandMaster));
        if (value.IsNullOrEmpty)
            return null;

        if (!UserServerScalingKeys.TryParseSessionOwnerToken(playerId, value.ToString(), out UserSessionOwner? owner))
            throw new InvalidOperationException("Redis contains an invalid session owner token.");
        return owner;
    }

    public async Task<bool> ReleaseSessionOwnerAsync(UserSessionOwner owner)
    {
        ValidateSessionOwner(owner);

        RedisResult result = await EvaluateAsync(
            ReleaseSessionOwnerScript,
            [
                UserServerScalingKeys.SessionOwner(owner.PlayerId),
                UserServerScalingKeys.SessionAcquireReceipt(owner.PlayerId, owner.SessionId)
            ],
            [owner.Token]);
        return (long)result == 1;
    }

    public async Task<bool> RestorePreviousSessionOwnerAsync(
        UserSessionOwnerAcquisition acquisition,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        ValidateSessionOwner(acquisition.CurrentOwner);
        if (acquisition.PreviousOwner != null)
            ValidateSessionOwner(acquisition.PreviousOwner);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        UserSessionOwner currentOwner = acquisition.CurrentOwner;
        UserSessionOwner? previousOwner = acquisition.PreviousOwner;
        RedisResult result = await EvaluateAsync(
            RestorePreviousSessionOwnerScript,
            [
                UserServerScalingKeys.SessionOwner(currentOwner.PlayerId),
                UserServerScalingKeys.SessionAcquireReceipt(
                    currentOwner.PlayerId,
                    currentOwner.SessionId),
                UserServerScalingKeys.NodeLease(previousOwner?.NodeId ?? currentOwner.NodeId)
            ],
            [
                currentOwner.Token,
                previousOwner?.NodeGeneration ?? string.Empty,
                previousOwner?.Token ?? string.Empty,
                lifetimeMilliseconds
            ]);
        return (long)result == 1;
    }

    public async Task<MatchingLeaderLease?> TryAcquireMatchingLeaderAsync(
        UserServerProcessIdentity identity,
        TimeSpan lifetime)
    {
        ValidateIdentity(identity);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            AcquireMatchingLeaderScript,
            [
                UserServerScalingKeys.NodeLease(identity.NodeId),
                UserServerScalingKeys.MatchingLeader,
                UserServerScalingKeys.MatchingLeaderFence
            ],
            [identity.Generation, identity.NodeId, lifetimeMilliseconds]);

        RedisResult[] values = RequireArray(result, 2, "matching leader acquisition");
        if ((long)values[0] != 1)
            return null;

        string leaderToken = RequireString(values[1], "matching leader token");
        if (!UserServerScalingKeys.TryParseMatchingLeaderToken(leaderToken, out MatchingLeaderLease? lease))
            throw new InvalidOperationException("Redis returned an invalid matching leader token.");
        return lease;
    }

    public async Task<bool> RenewMatchingLeaderAsync(MatchingLeaderLease lease, TimeSpan lifetime)
    {
        ValidateMatchingLeader(lease);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        RedisResult result = await EvaluateAsync(
            RenewMatchingLeaderScript,
            [
                UserServerScalingKeys.NodeLease(lease.NodeId),
                UserServerScalingKeys.MatchingLeader
            ],
            [lease.NodeGeneration, lease.Token, lifetimeMilliseconds]);
        return (long)result == 1;
    }

    public async Task<MatchingLeaderLease?> GetMatchingLeaderAsync()
    {
        RedisValue value = await redisPool.ExecuteWithRetryAsync(
            database => database.StringGetAsync(
                UserServerScalingKeys.MatchingLeader,
                CommandFlags.DemandMaster));
        if (value.IsNullOrEmpty)
            return null;

        if (!UserServerScalingKeys.TryParseMatchingLeaderToken(value.ToString(), out MatchingLeaderLease? lease))
            throw new InvalidOperationException("Redis contains an invalid matching leader token.");
        return lease;
    }

    public async Task<bool> ReleaseMatchingLeaderAsync(MatchingLeaderLease lease)
    {
        ValidateMatchingLeader(lease);

        RedisResult result = await EvaluateAsync(
            ReleaseIfEqualsScript,
            [UserServerScalingKeys.MatchingLeader],
            [lease.Token]);
        return (long)result == 1;
    }

    public async Task<bool> TryRegisterMatchingAdmissionRecoveryAsync(
        MatchingLeaderLease? lease,
        MatchingAdmissionRecoveryRecord record,
        TimeSpan lifetime)
    {
        if (lease != null)
            ValidateMatchingLeader(lease);
        ValidateMatchingAdmissionRecovery(record);
        long lifetimeMilliseconds = ValidateLifetime(lifetime);

        byte[] serializedRecord = MessagePackSerializer.Serialize(
            record,
            RecoverySerializerOptions);
        string matchingId = record.MatchingId.ToString(CultureInfo.InvariantCulture);
        RedisResult result = await EvaluateAsync(
            RegisterMatchingAdmissionRecoveryScript,
            [
                UserServerScalingKeys.NodeLease(lease?.NodeId ?? "single"),
                UserServerScalingKeys.MatchingLeader,
                MatchingHandoffRedisKeys.AdmissionStateKey(record.MatchingId),
                UserServerScalingKeys.MatchingAdmissionRecovery(record.MatchingId),
                UserServerScalingKeys.MatchingAdmissionRecoveryDeadlines
            ],
            [
                lease?.NodeGeneration ?? string.Empty,
                lease?.Token ?? string.Empty,
                serializedRecord,
                MatchingHandoffRedisKeys.AdmissionPendingState,
                record.DeadlineUnixMilliseconds,
                lifetimeMilliseconds,
                matchingId
            ]);

        long status = (long)result;
        if (status == -1)
            return false;
        if (status == -2)
        {
            throw new InvalidOperationException(
                $"Matching admission recovery {record.MatchingId} already has different metadata.");
        }
        if (status == -3)
        {
            throw new InvalidOperationException(
                $"Matching admission recovery {record.MatchingId} is no longer pending.");
        }
        if (status != 1)
            throw new InvalidOperationException("Redis returned an invalid admission recovery registration status.");
        return true;
    }

    public async Task<IReadOnlyList<long>> GetExpiredMatchingAdmissionRecoveryIdsAsync(
        DateTimeOffset now,
        int maximumCount)
    {
        if (now <= DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(now));
        if (maximumCount is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        RedisValue[] values = await redisPool.ExecuteWithRetryAsync(
            database => database.SortedSetRangeByScoreAsync(
                UserServerScalingKeys.MatchingAdmissionRecoveryDeadlines,
                double.NegativeInfinity,
                now.ToUnixTimeMilliseconds(),
                Exclude.None,
                Order.Ascending,
                0,
                maximumCount,
                CommandFlags.DemandMaster));
        var matchingIds = new List<long>(values.Length);
        foreach (RedisValue value in values)
        {
            if (!long.TryParse(
                    value.ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long matchingId) ||
                matchingId <= 0)
            {
                throw new InvalidOperationException(
                    "Redis contains an invalid matching admission recovery deadline member.");
            }

            matchingIds.Add(matchingId);
        }

        return matchingIds;
    }

    public async Task<MatchingAdmissionRecoveryRecord?> GetMatchingAdmissionRecoveryAsync(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        RedisValue value = await redisPool.ExecuteWithRetryAsync(
            database => database.StringGetAsync(
                UserServerScalingKeys.MatchingAdmissionRecovery(matchingId),
                CommandFlags.DemandMaster));
        if (value.IsNullOrEmpty)
            return null;

        MatchingAdmissionRecoveryRecord? record;
        try
        {
            record = MessagePackSerializer.Deserialize<MatchingAdmissionRecoveryRecord>(
                (byte[])value!,
                RecoverySerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidOperationException(
                $"Redis contains malformed matching admission recovery metadata for match {matchingId}.",
                ex);
        }

        if (record == null || record.MatchingId != matchingId || !record.IsValid)
        {
            throw new InvalidOperationException(
                $"Redis contains invalid matching admission recovery metadata for match {matchingId}.");
        }

        return record;
    }

    public async Task<bool> TryUpdateMatchingAdmissionRecoveryAsync(
        MatchingLeaderLease? lease,
        MatchingAdmissionRecoveryRecord expectedRecord,
        MatchingAdmissionRecoveryRecord updatedRecord,
        TimeSpan lifetime)
    {
        if (lease != null)
            ValidateMatchingLeader(lease);
        ValidateMatchingAdmissionRecovery(expectedRecord);
        ValidateMatchingAdmissionRecovery(updatedRecord);
        if (!HaveSameRecoveryIdentity(expectedRecord, updatedRecord) ||
            (expectedRecord.AdmissionCompleted && !updatedRecord.AdmissionCompleted) ||
            updatedRecord.GameServerOwnerLossObservedUnixMilliseconds <
            expectedRecord.GameServerOwnerLossObservedUnixMilliseconds ||
            updatedRecord.CleanupMode < expectedRecord.CleanupMode)
        {
            throw new ArgumentException(
                "Updated matching admission recovery metadata changed its exact identity or moved backward.",
                nameof(updatedRecord));
        }

        long lifetimeMilliseconds = ValidateLifetime(lifetime);
        byte[] expectedPayload = MessagePackSerializer.Serialize(
            expectedRecord,
            RecoverySerializerOptions);
        byte[] updatedPayload = MessagePackSerializer.Serialize(
            updatedRecord,
            RecoverySerializerOptions);
        RedisResult result = await EvaluateAsync(
            UpdateMatchingAdmissionRecoveryScript,
            [
                UserServerScalingKeys.NodeLease(lease?.NodeId ?? "single"),
                UserServerScalingKeys.MatchingLeader,
                UserServerScalingKeys.MatchingAdmissionRecovery(expectedRecord.MatchingId),
                UserServerScalingKeys.MatchingAdmissionRecoveryDeadlines
            ],
            [
                lease?.NodeGeneration ?? string.Empty,
                lease?.Token ?? string.Empty,
                expectedPayload,
                updatedPayload,
                updatedRecord.DeadlineUnixMilliseconds,
                lifetimeMilliseconds,
                updatedRecord.MatchingId.ToString(CultureInfo.InvariantCulture)
            ]);
        long status = (long)result;
        if (status is -1 or 0)
            return false;
        if (status != 1)
            throw new InvalidOperationException("Redis returned an invalid admission recovery update status.");
        return true;
    }

    public async Task RemoveMatchingAdmissionRecoveryAsync(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        await EvaluateAsync(
            RemoveMatchingAdmissionRecoveryScript,
            [
                UserServerScalingKeys.MatchingAdmissionRecovery(matchingId),
                UserServerScalingKeys.MatchingAdmissionRecoveryDeadlines
            ],
            [matchingId.ToString(CultureInfo.InvariantCulture)]);
    }

    public async Task<DateTimeOffset> GetRedisTimeAsync()
    {
        RedisResult result = await EvaluateAsync(
            GetRedisTimeScript,
            [],
            []);
        RedisResult[] values = RequireArray(result, 2, "Redis server time");
        if (!long.TryParse(
                values[0].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long seconds) ||
            !long.TryParse(
                values[1].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long microseconds) ||
            seconds < 0 ||
            microseconds is < 0 or >= 1_000_000)
        {
            throw new InvalidOperationException("Redis returned an invalid server time.");
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(microseconds * 10);
    }

    public async Task<MatchingLifecycleApplyResult> ApplyMatchingLifecycleOnceAsync(
        string eventId,
        MatchingLifecycleEffect effect,
        long playerId,
        long matchingId,
        DateTimeOffset occurredAt,
        TimeSpan markerLifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        ValidatePlayerId(playerId);
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        if (occurredAt <= DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(occurredAt));

        long markerLifetimeMilliseconds = ValidateLifetime(markerLifetime);
        string eventFingerprint = UserServerScalingKeys.FingerprintEventId(eventId);
        string markerValue =
            $"{((int)effect).ToString(CultureInfo.InvariantCulture)}|{eventFingerprint}";
        string playerField = playerId.ToString(CultureInfo.InvariantCulture);
        string matchingValue = matchingId.ToString(CultureInfo.InvariantCulture);

        RedisResult result = await EvaluateAsync(
            ApplyMatchingLifecycleOnceScript,
            [
                UserServerScalingKeys.MatchingLifecycleTerminal(playerId, matchingId),
                MatchingHandoffRedisKeys.ClaimKey(playerId),
                "leave_penalties",
                "leave_penalty_decay_at"
            ],
            [
                matchingValue,
                playerField,
                ((int)effect).ToString(CultureInfo.InvariantCulture),
                markerValue,
                occurredAt.ToUnixTimeSeconds(),
                markerLifetimeMilliseconds
            ]);

        RedisResult[] values = RequireArray(result, 3, "matching lifecycle application");
        long status = (long)values[0];
        if (status == -2)
            throw new InvalidOperationException("Redis contains an invalid binary leave penalty value.");
        if (status is not (0 or 1))
            throw new InvalidOperationException("Redis returned an invalid matching lifecycle status.");

        string effectiveMarker = RequireString(values[2], "matching lifecycle terminal marker");
        string[] markerParts = effectiveMarker.Split('|');
        if (markerParts.Length != 2 ||
            !int.TryParse(markerParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int effectiveEffectValue) ||
            !Enum.IsDefined(typeof(MatchingLifecycleEffect), effectiveEffectValue) ||
            markerParts[1].Length != 64)
        {
            throw new InvalidOperationException("Redis contains an invalid matching lifecycle terminal marker.");
        }

        var effectiveEffect = (MatchingLifecycleEffect)effectiveEffectValue;
        bool conflicting =
            effectiveEffect != effect ||
            !string.Equals(markerParts[1], eventFingerprint, StringComparison.Ordinal);
        return new MatchingLifecycleApplyResult(
            status == 1,
            (long)values[1] == 1,
            effectiveEffect,
            markerParts[1],
            conflicting);
    }

    private Task<RedisResult> EvaluateAsync(
        string script,
        RedisKey[] keys,
        RedisValue[] values)
    {
        // Every script is idempotent for the same exact process/session token. Retrying a lost
        // response therefore renews or returns the existing token instead of allocating a new one.
        return redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                script,
                keys,
                values,
                CommandFlags.DemandMaster));
    }

    private static RedisResult[] RequireArray(RedisResult result, int expectedLength, string operation)
    {
        RedisResult[] values = (RedisResult[])result!;
        if (values.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"Redis returned an invalid result for {operation}: expected {expectedLength} values, got {values.Length}.");
        }

        return values;
    }

    private static string RequireString(RedisResult result, string valueName)
    {
        if (result.IsNull || string.IsNullOrWhiteSpace(result.ToString()))
            throw new InvalidOperationException($"Redis returned an empty {valueName}.");
        return result.ToString();
    }

    private static void ValidateIdentity(UserServerProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsValid)
            throw new ArgumentException("UserServer process identity is invalid.", nameof(identity));
    }

    private static void ValidateSessionOwner(UserSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            throw new ArgumentException("Session owner is invalid.", nameof(owner));
    }

    private static void ValidateMatchingLeader(MatchingLeaderLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsValid)
            throw new ArgumentException("Matching leader lease is invalid.", nameof(lease));
    }

    private static void ValidateMatchingAdmissionRecovery(MatchingAdmissionRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsValid)
        {
            throw new ArgumentException(
                "Matching admission recovery metadata is invalid.",
                nameof(record));
        }
    }

    private static bool HaveSameRecoveryIdentity(
        MatchingAdmissionRecoveryRecord first,
        MatchingAdmissionRecoveryRecord second)
    {
        if (first.MatchingId != second.MatchingId ||
            !string.Equals(first.GameServerNodeId, second.GameServerNodeId, StringComparison.Ordinal) ||
            !string.Equals(first.GameServerGeneration, second.GameServerGeneration, StringComparison.Ordinal) ||
            first.GameServerFence != second.GameServerFence ||
            first.Players.Count != second.Players.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Players.Count; index++)
        {
            MatchingAdmissionRecoveryRoute firstRoute = first.Players[index];
            MatchingAdmissionRecoveryRoute secondRoute = second.Players[index];
            if (firstRoute.PlayerId != secondRoute.PlayerId ||
                !string.Equals(firstRoute.OwnerNodeId, secondRoute.OwnerNodeId, StringComparison.Ordinal) ||
                !string.Equals(firstRoute.OwnerNodeGeneration, secondRoute.OwnerNodeGeneration,
                    StringComparison.Ordinal) ||
                !string.Equals(firstRoute.OwnerSessionId, secondRoute.OwnerSessionId, StringComparison.Ordinal) ||
                firstRoute.OwnerSessionGeneration != secondRoute.OwnerSessionGeneration ||
                !string.Equals(firstRoute.RequestId, secondRoute.RequestId, StringComparison.Ordinal) ||
                !firstRoute.QueueEntry.AsSpan().SequenceEqual(secondRoute.QueueEntry))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePlayerId(long playerId)
    {
        if (playerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerId));
    }

    private static void ValidateTokenComponent(string value, string parameterName)
    {
        if (!UserServerClusterOptions.IsSafeTokenComponent(value))
        {
            throw new ArgumentException(
                "Value must contain only letters, numbers, '_' or '-' and be at most 64 characters.",
                parameterName);
        }
    }

    private static long ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        return checked((long)lifetime.TotalMilliseconds);
    }
}
