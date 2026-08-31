using MessagePack;
using network.contracts.authentication;
using network.contracts.scaling;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure.authentication;

/// <summary>
///     Persists one-time game handoff tickets and owns the owner-fenced consume and receipt-reconciliation
///     transitions that must execute atomically in Redis.
/// </summary>
public sealed class RedisGameHandoffTicketStore(
    ICacheHelper cacheHelper,
    IRedisConnectionPool redisPool) : IGameHandoffTicketStore
{
    private const string TicketKeyPrefix = "game_handoff_ticket:";
    private const int ConsumeResolutionAttempts = 3;

    private static readonly TimeSpan MinimumRedisLifetime = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaximumConsumeReceiptLifetime = TimeSpan.FromMinutes(10);

    private const string GuardedConsumeWithReceiptScript = """
        if redis.call('EXISTS', KEYS[5]) ~= 0 then
            if redis.call('EXISTS', KEYS[1]) ~= 0 then
                return {-1}
            end
            local receiptNonce = redis.call('HGET', KEYS[5], 'nonce')
            local receiptStatus = redis.call('HGET', KEYS[5], 'status')
            local receiptContext = redis.call('HGET', KEYS[5], 'context')
            if receiptNonce ~= ARGV[5] or not receiptStatus then
                return {-1}
            end
            if receiptStatus == 'consumed' then
                if not receiptContext then
                    return {-1}
                end
                return {2, receiptContext}
            end
            if receiptStatus == 'aborted' then
                if receiptContext then
                    return {-1}
                end
                return {3}
            end
            return {-1}
        end

        if redis.call('GET', KEYS[2]) ~= ARGV[1] then
            return {0}
        end
        if redis.call('GET', KEYS[3]) ~= ARGV[2] then
            return {0}
        end

        local value = redis.call('GET', KEYS[1])
        if not value then
            return {0}
        end

        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        redis.call('PEXPIRE', KEYS[3], ARGV[3])
        redis.call('ZADD', KEYS[4], now + tonumber(ARGV[3]), ARGV[4])
        local desiredTtl = tonumber(ARGV[3]) + 3600000
        local currentTtl = redis.call('PTTL', KEYS[4])
        if currentTtl < desiredTtl then
            redis.call('PEXPIRE', KEYS[4], desiredTtl)
        end

        redis.call(
            'HSET',
            KEYS[5],
            'nonce',
            ARGV[5],
            'status',
            'consumed',
            'context',
            value)
        redis.call('PEXPIRE', KEYS[5], ARGV[6])
        redis.call('DEL', KEYS[1])
        return {1, value}
        """;

    private const string ReconcileConsumeReceiptScript = """
        if redis.call('EXISTS', KEYS[2]) ~= 0 then
            if redis.call('EXISTS', KEYS[1]) ~= 0 then
                return {-1}
            end
            local receiptNonce = redis.call('HGET', KEYS[2], 'nonce')
            local receiptStatus = redis.call('HGET', KEYS[2], 'status')
            local receiptContext = redis.call('HGET', KEYS[2], 'context')
            if receiptNonce ~= ARGV[1] or not receiptStatus then
                return {-1}
            end
            if receiptStatus == 'consumed' then
                if not receiptContext then
                    return {-1}
                end
                return {1, receiptContext}
            end
            if receiptStatus == 'aborted' then
                if receiptContext then
                    return {-1}
                end
                return {2}
            end
            return {-1}
        end

        redis.call(
            'HSET',
            KEYS[2],
            'nonce',
            ARGV[1],
            'status',
            'aborted')
        redis.call('PEXPIRE', KEYS[2], ARGV[2])
        redis.call('DEL', KEYS[1])
        return {2}
        """;

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime)
    {
        ValidateRedisLifetime(lifetime, nameof(lifetime));
        byte[] serializedContext = MessagePackSerializer.Serialize(context, SerializerOptions);
        return cacheHelper.StringSetIfNotExistsAsync(
            context.HasGameServerOwner
                ? GameServerRoutingKeys.OwnedHandoffTicket(ticketHash)
                : MakeLegacyKey(ticketHash),
            serializedContext,
            lifetime);
    }

    public async Task<GameHandoffContext?> ConsumeAsync(string ticketHash)
    {
        RedisValue serializedContext = await cacheHelper.StringGetDeleteAsync(MakeLegacyKey(ticketHash));
        if (serializedContext.IsNullOrEmpty)
            return null;

        return MessagePackSerializer.Deserialize<GameHandoffContext>(
            (byte[])serializedContext!,
            SerializerOptions);
    }

    public async Task<GameHandoffContext?> PeekOwnedAsync(string ticketHash)
    {
        RedisValue serializedContext =
            await cacheHelper.StringGetAsync(GameServerRoutingKeys.OwnedHandoffTicket(ticketHash));
        return Deserialize(serializedContext);
    }

    public async Task<GameHandoffContext?> ConsumeOwnedAsync(
        string ticketHash,
        string consumeNonce,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner,
        TimeSpan provisionalOwnerLifetime,
        TimeSpan receiptLifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(owner);
        if (!identity.IsValid || !owner.IsValid ||
            string.IsNullOrWhiteSpace(consumeNonce) || consumeNonce.Length > 128 ||
            provisionalOwnerLifetime < MinimumRedisLifetime ||
            receiptLifetime < MinimumRedisLifetime ||
            !string.Equals(identity.NodeId, owner.NodeId, StringComparison.Ordinal) ||
            !string.Equals(identity.Generation, owner.Generation, StringComparison.Ordinal))
        {
            return null;
        }

        string ticketKey = GameServerRoutingKeys.OwnedHandoffTicket(ticketHash);
        string receiptKey = GameServerRoutingKeys.OwnedHandoffConsumeReceipt(
            ticketHash,
            consumeNonce);
        List<Exception>? ambiguousExceptions = null;
        for (int attempt = 1; attempt <= ConsumeResolutionAttempts; attempt++)
        {
            try
            {
                RedisValue serializedContext =
                    await ConsumeOwnedAtomicallyAsync(
                        ticketKey,
                        GameServerRoutingKeys.NodeLease(owner.NodeId),
                        identity.Generation,
                        GameServerRoutingKeys.MatchOwner(owner.MatchingId),
                        owner.Token,
                        provisionalOwnerLifetime,
                        GameServerRoutingKeys.NodeSlots(owner.NodeId, owner.Generation),
                        owner.MatchingId,
                        receiptKey,
                        consumeNonce,
                        receiptLifetime);
                return DeserializeOwnedContextOrNull(serializedContext, identity, owner);
            }
            catch (Exception consumeException) when (
                consumeException is RedisTimeoutException or RedisConnectionException)
            {
                ambiguousExceptions ??= [];
                ambiguousExceptions.Add(consumeException);
                if (attempt < ConsumeResolutionAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        for (int attempt = 1; attempt <= ConsumeResolutionAttempts; attempt++)
        {
            try
            {
                RedisValue reconciledContext =
                    await ReconcileConsumeReceiptAsync(
                        ticketKey,
                        receiptKey,
                        consumeNonce,
                        receiptLifetime);
                return DeserializeOwnedContextOrNull(reconciledContext, identity, owner);
            }
            catch (Exception reconcileException) when (
                reconcileException is RedisTimeoutException or RedisConnectionException)
            {
                ambiguousExceptions!.Add(reconcileException);
                if (attempt < ConsumeResolutionAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        throw new InvalidOperationException(
            "An owner-bound handoff consume result could not be resolved or causally aborted.",
            new AggregateException(ambiguousExceptions!));
    }

    private async Task<RedisValue> ConsumeOwnedAtomicallyAsync(
        string valueKey,
        string firstGuardKey,
        string expectedFirstGuardValue,
        string secondGuardKey,
        string expectedSecondGuardValue,
        TimeSpan secondGuardExpiry,
        string ownerSlotsKey,
        long matchingId,
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry)
    {
        ValidateRedisLifetime(secondGuardExpiry, nameof(secondGuardExpiry));
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        ValidateReceipt(receiptKey, consumeNonce, receiptExpiry);

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                GuardedConsumeWithReceiptScript,
                [valueKey, firstGuardKey, secondGuardKey, ownerSlotsKey, receiptKey],
                [
                    expectedFirstGuardValue,
                    expectedSecondGuardValue,
                    checked((long)secondGuardExpiry.TotalMilliseconds),
                    matchingId,
                    consumeNonce,
                    checked((long)receiptExpiry.TotalMilliseconds)
                ],
                CommandFlags.DemandMaster),
            retryCount: 1);

        RedisResult[] response = (RedisResult[])result!;
        if (response.Length == 0)
            throw new InvalidOperationException("Redis returned an empty guarded consume response.");

        long status = (long)response[0];
        if (status is 0 or 3)
        {
            if (response.Length != 1)
                throw new InvalidOperationException($"Redis returned invalid guarded consume status {status}.");
            return RedisValue.Null;
        }
        if (status == -1)
        {
            throw new InvalidOperationException(
                "Redis contains a conflicting or incomplete guarded consume receipt.");
        }
        if (status is not (1 or 2) || response.Length != 2 || response[1].IsNull)
            throw new InvalidOperationException($"Redis returned invalid guarded consume status {status}.");
        return (RedisValue)(byte[])response[1]!;
    }

    private async Task<RedisValue> ReconcileConsumeReceiptAsync(
        string valueKey,
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry)
    {
        ValidateReceipt(receiptKey, consumeNonce, receiptExpiry);

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                ReconcileConsumeReceiptScript,
                [valueKey, receiptKey],
                [consumeNonce, checked((long)receiptExpiry.TotalMilliseconds)],
                CommandFlags.DemandMaster),
            retryCount: 1);

        RedisResult[] response = (RedisResult[])result!;
        if (response.Length == 0)
            throw new InvalidOperationException("Redis returned an empty consume reconciliation response.");

        long status = (long)response[0];
        if (status == 2 && response.Length == 1)
            return RedisValue.Null;
        if (status != 1 || response.Length != 2 || response[1].IsNull)
        {
            throw new InvalidOperationException(
                "Redis contains a conflicting or incomplete guarded consume receipt.");
        }
        return (RedisValue)(byte[])response[1]!;
    }

    private static void ValidateReceipt(
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry)
    {
        if (string.IsNullOrWhiteSpace(receiptKey))
            throw new ArgumentException("A receipt key is required.", nameof(receiptKey));
        if (string.IsNullOrWhiteSpace(consumeNonce) || consumeNonce.Length > 128)
            throw new ArgumentException("A bounded consume nonce is required.", nameof(consumeNonce));
        if (receiptExpiry < MinimumRedisLifetime || receiptExpiry > MaximumConsumeReceiptLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(receiptExpiry),
                $"Receipt expiry must be at least {MinimumRedisLifetime.TotalMilliseconds:0} millisecond and no more than " +
                $"{MaximumConsumeReceiptLifetime.TotalMinutes} minutes.");
        }
    }

    private static void ValidateRedisLifetime(TimeSpan lifetime, string parameterName)
    {
        if (lifetime < MinimumRedisLifetime)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Redis lifetimes must be at least {MinimumRedisLifetime.TotalMilliseconds:0} millisecond.");
        }
    }

    private static GameHandoffContext? Deserialize(RedisValue serializedContext)
    {
        if (serializedContext.IsNullOrEmpty)
            return null;
        try
        {
            return MessagePackSerializer.Deserialize<GameHandoffContext>(
                (byte[])serializedContext!,
                SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidOperationException("Redis contains a malformed game handoff context.", ex);
        }
    }

    private static GameHandoffContext? DeserializeOwnedContext(
        RedisValue serializedContext,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner)
    {
        GameHandoffContext? context = Deserialize(serializedContext);
        if (context == null)
            return null;

        GameServerMatchOwner? contextOwner = context.GetGameServerOwner();
        if (contextOwner != owner ||
            !string.Equals(contextOwner.NodeId, identity.NodeId, StringComparison.Ordinal) ||
            !string.Equals(contextOwner.Generation, identity.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Redis returned a handoff consume receipt for a different GameServer owner.");
        }

        return context;
    }

    private static GameHandoffContext? DeserializeOwnedContextOrNull(
        RedisValue serializedContext,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner)
    {
        if (serializedContext.IsNull)
            return null;

        return DeserializeOwnedContext(serializedContext, identity, owner)
               ?? throw new InvalidOperationException(
                   "Redis returned an empty owner-bound handoff consume context.");
    }

    private static string MakeLegacyKey(string ticketHash)
    {
        return TicketKeyPrefix + ticketHash;
    }
}
