using network.common;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure;

public class CacheHelper(IRedisConnectionPool redisPool) : ICacheHelper
{
    private static readonly TimeSpan MaximumGuardedConsumeReceiptLifetime = TimeSpan.FromMinutes(10);

    public IRedLockFactory GetRedLockFactory()
    {
        return redisPool.GetRedLockFactory();
    }

    public Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashSetAsync(key, field, value), db);
    }

    public Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashSetAsync(key, field, value), db);
    }

    public async Task HashSetWithExpiryAsync(
        string key,
        string field,
        byte[] value,
        TimeSpan expiry,
        int db = -1)
    {
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be greater than zero.");

        const string setWithExpiryScript =
            "redis.call('HSET', KEYS[1], ARGV[1], ARGV[2]); " +
            "redis.call('PEXPIRE', KEYS[1], ARGV[3]); return 1";

        await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                setWithExpiryScript,
                [key],
                [field, value, checked((long)expiry.TotalMilliseconds)],
                CommandFlags.DemandMaster),
            db);
    }

    public async Task HashSetPairAtomicAsync(
        string firstKey,
        string firstField,
        RedisValue firstValue,
        string secondKey,
        string secondField,
        RedisValue secondValue,
        int db = -1
    )
    {
        const string setPairScript =
            "redis.call('HSET', KEYS[1], ARGV[1], ARGV[2]); " +
            "redis.call('HSET', KEYS[2], ARGV[3], ARGV[4]); return 1";

        await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                setPairScript,
                [firstKey, secondKey],
                [firstField, firstValue, secondField, secondValue],
                CommandFlags.DemandMaster
            ),
            db
        );
    }

    public Task<RedisValue> HashGetAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.DemandMaster), db);
    }

    public Task<RedisValue> HashGetAsync(string key, long field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.DemandMaster), db);
    }

    public async Task<RedisValue> HashGetDeleteFirstAsync(
        string firstKey,
        RedisValue firstField,
        string secondKey,
        RedisValue secondField,
        int db = -1
    )
    {
        const string getDeleteFirstScript =
            "local value = redis.call('HGET', KEYS[1], ARGV[1]); " +
            "if not value then value = redis.call('HGET', KEYS[2], ARGV[2]); end; " +
            "redis.call('HDEL', KEYS[1], ARGV[1]); " +
            "redis.call('HDEL', KEYS[2], ARGV[2]); return value";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                getDeleteFirstScript,
                [firstKey, secondKey],
                [firstField, secondField],
                CommandFlags.DemandMaster
            ),
            db
        );
        return result.IsNull ? RedisValue.Null : (RedisValue)(byte[])result!;
    }

    public Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1)
    {
        return ExecuteRedisCommandAsync(
            async database =>
            {
                var batch = database.CreateBatch();
                var task = batch.HashGetAsync(key, fields, CommandFlags.DemandMaster);
                batch.Execute();
                return await task;
            },
            db
        );
    }

    public Task<HashEntry[]> HashGetAllAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAllAsync(key, CommandFlags.DemandMaster), db);
    }

    public Task<RedisValue> HashGetFromReplicaAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.PreferReplica), db);
    }

    public Task<RedisValue> HashGetFromReplicaAsync(string key, long field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.PreferReplica), db);
    }

    public Task<RedisValue[]> HashGetFromReplicaAsync(string key, RedisValue[] fields, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, fields, CommandFlags.PreferReplica), db);
    }

    public Task<HashEntry[]> HashGetAllFromReplicaAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAllAsync(key, CommandFlags.PreferReplica), db);
    }

    public Task<bool> HashDeleteAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashDeleteAsync(key, field), db);
    }

    public Task<bool> HashDeleteAsync(string key, long field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashDeleteAsync(key, field), db);
    }

    public Task<bool> HashExistsAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashExistsAsync(key, field, CommandFlags.DemandMaster),
            db);
    }

    public Task<bool> HashExistsOnReplicaAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashExistsAsync(key, field, CommandFlags.PreferReplica),
            db);
    }

    public Task<RedisValue[]> ListRangeAsync(
        string key,
        int start = 0,
        int end = -1,
        int db = -1
    )
    {
        return ExecuteRedisCommandAsync(
            database => database.ListRangeAsync(key, start, end, CommandFlags.DemandMaster), db);
    }

    public Task<RedisValue[]> ListRangeFromReplicaAsync(
        string key,
        int start = 0,
        int end = -1,
        int db = -1
    )
    {
        return ExecuteRedisCommandAsync(
            database => database.ListRangeAsync(key, start, end, CommandFlags.PreferReplica), db);
    }

    public async Task<RedisValue[]> ListRangeAsync(List<string> keys, int start = 0, int end = -1, int db = -1)
    {
        if (keys.Count == 0) return [];

        var results = new List<RedisValue>();

        for (int i = 0; i < keys.Count; i += Config.BATCH_SIZE)
        {
            var batchTasks = keys.Skip(i)
                .Take(Config.BATCH_SIZE)
                .Select(key =>
                    ExecuteRedisCommandAsync(
                        database =>
                            database.ListRangeAsync(
                                key,
                                start,
                                end,
                                CommandFlags.DemandMaster
                            ),
                        db
                    )
                );

            var batchResults = await Task.WhenAll(batchTasks);
            results.AddRange(batchResults.SelectMany(r => r));
        }

        return results.ToArray();
    }

    public async Task<List<(string key, RedisValue value)>> ListRangeWithKeyAsync(List<string> keys, int start = 0,
        int end = -1, int db = -1)
    {
        if (keys.Count == 0) return [];

        var results = new List<(string key, RedisValue value)>();
        for (int i = 0; i < keys.Count; i += Config.BATCH_SIZE)
        {
            var batchTasks = keys.Skip(i)
                .Take(Config.BATCH_SIZE)
                .Select(async key =>
                {
                    var range = await ExecuteRedisCommandAsync(
                        database =>
                            database.ListRangeAsync(
                                key,
                                start,
                                end,
                                CommandFlags.DemandMaster
                            ),
                        db
                    );
                    return range.Select(value => (key, value));
                });

            var batchResults = await Task.WhenAll(batchTasks);
            results.AddRange(batchResults.SelectMany(x => x));
        }

        return results;
    }

    public Task<long> ListPushAsync(string key, string value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.ListRightPushAsync(key, value), db);
    }

    public Task<long> ListRemoveAsync(string key, string value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.ListRemoveAsync(key, value), db);
    }

    public Task<long> EnqueueAsync(string key, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.ListLeftPushAsync(key, value), db);
    }

    public async Task<byte[]?> DequeueAsync(string key, int db = -1)
    {
        var result = await ExecuteRedisCommandAsync(database => database.ListRightPopAsync(key), db);
        return result.HasValue ? (byte[])result! : null;
    }

    public Task<long> ListLengthAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.ListLengthAsync(key), db);
    }

    public Task<long> StringIncrementAsync(string key, int db = -1)
    {
        return ExecuteAtomicRedisCommandAsync(database => database.StringIncrementAsync(key), db);
    }

    public Task<long> StringIncrementByAsync(string key, long increment, int db = -1)
    {
        return ExecuteAtomicRedisCommandAsync(database => database.StringIncrementAsync(key, increment), db);
    }

    public Task<RedisValue> StringGetAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(
            database => database.StringGetAsync(key, CommandFlags.DemandMaster),
            db
        );
    }

    public Task<RedisValue> StringGetDeleteAsync(string key, int db = -1)
    {
        return ExecuteAtomicRedisCommandAsync(
            database => database.StringGetDeleteAsync(key, CommandFlags.DemandMaster),
            db
        );
    }

    public Task<bool> StringSetAsync(
        string key,
        RedisValue value,
        TimeSpan? expiry = null,
        int db = -1)
    {
        return ExecuteRedisCommandAsync(
            database => database.StringSetAsync(
                key,
                value,
                expiry,
                When.Always,
                CommandFlags.DemandMaster),
            db);
    }

    public Task<bool> StringSetIfNotExistsAsync(
        string key,
        RedisValue value,
        TimeSpan? expiry = null,
        int db = -1
    )
    {
        return ExecuteAtomicRedisCommandAsync(
            database => database.StringSetAsync(
                key,
                value,
                expiry,
                When.NotExists,
                CommandFlags.DemandMaster
            ),
            db
        );
    }

    public async Task<bool> StringSetIfEqualsAsync(
        string key,
        string expectedValue,
        string newValue,
        TimeSpan expiry,
        int db = -1
    )
    {
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be greater than zero.");

        const string setIfEqualsScript =
            "if redis.call('GET', KEYS[1]) == ARGV[1] then " +
            "redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]); return 1 else return 0 end";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                setIfEqualsScript,
                [key],
                [expectedValue, newValue, checked((long)expiry.TotalMilliseconds)],
                CommandFlags.DemandMaster
            ),
            db
        );
        return (long)result == 1;
    }

    public async Task<bool> StringDeleteIfEqualsAsync(string key, string expectedValue, int db = -1)
    {
        ArgumentNullException.ThrowIfNull(expectedValue);

        const string deleteIfEqualsScript =
            "if redis.call('GET', KEYS[1]) == ARGV[1] then " +
            "return redis.call('DEL', KEYS[1]) else return 0 end";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                deleteIfEqualsScript,
                [key],
                [expectedValue],
                CommandFlags.DemandMaster
            ),
            db
        );
        return (long)result == 1;
    }

    public async Task<bool> StringSetWithExpiryIfGuardEqualsAsync(
        string key,
        RedisValue value,
        TimeSpan expiry,
        string guardKey,
        string expectedGuardValue,
        int db = -1)
    {
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be greater than zero.");

        const string setIfGuardEqualsScript =
            "if redis.call('GET', KEYS[2]) ~= ARGV[1] then return 0 end; " +
            "redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]); return 1";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                setIfGuardEqualsScript,
                [key, guardKey],
                [expectedGuardValue, value, checked((long)expiry.TotalMilliseconds)],
                CommandFlags.DemandMaster),
            db);
        return (long)result == 1;
    }

    public async Task<RedisValue> StringGetDeleteIfGuardsEqualAsync(
        string valueKey,
        string firstGuardKey,
        string expectedFirstGuardValue,
        string secondGuardKey,
        string expectedSecondGuardValue,
        TimeSpan secondGuardExpiry,
        string ownerSlotsKey,
        long matchingId,
        int db = -1)
    {
        if (secondGuardExpiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(secondGuardExpiry), "Expiry must be greater than zero.");
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        const string guardedConsumeScript =
            "if redis.call('GET', KEYS[2]) ~= ARGV[1] then return false end; " +
            "if redis.call('GET', KEYS[3]) ~= ARGV[2] then return false end; " +
            "local value = redis.call('GET', KEYS[1]); if not value then return false end; " +
            "local redisTime = redis.call('TIME'); " +
            "local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000); " +
            "redis.call('PEXPIRE', KEYS[3], ARGV[3]); " +
            "redis.call('ZADD', KEYS[4], now + tonumber(ARGV[3]), ARGV[4]); " +
            "local desiredTtl = tonumber(ARGV[3]) + 3600000; " +
            "local currentTtl = redis.call('PTTL', KEYS[4]); " +
            "if currentTtl < desiredTtl then redis.call('PEXPIRE', KEYS[4], desiredTtl) end; " +
            "redis.call('DEL', KEYS[1]); return value";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                guardedConsumeScript,
                [valueKey, firstGuardKey, secondGuardKey, ownerSlotsKey],
                [
                    expectedFirstGuardValue,
                    expectedSecondGuardValue,
                    checked((long)secondGuardExpiry.TotalMilliseconds),
                    matchingId
                ],
                CommandFlags.DemandMaster),
            db);
        return result.IsNull ? RedisValue.Null : (RedisValue)(byte[])result!;
    }

    public async Task<RedisValue> StringGetDeleteIfGuardsEqualWithReceiptAsync(
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
        TimeSpan receiptExpiry,
        int db = -1)
    {
        if (secondGuardExpiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(secondGuardExpiry), "Expiry must be greater than zero.");
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        if (string.IsNullOrWhiteSpace(receiptKey))
            throw new ArgumentException("A receipt key is required.", nameof(receiptKey));
        if (string.IsNullOrWhiteSpace(consumeNonce) || consumeNonce.Length > 128)
            throw new ArgumentException("A bounded consume nonce is required.", nameof(consumeNonce));
        if (receiptExpiry <= TimeSpan.Zero || receiptExpiry > MaximumGuardedConsumeReceiptLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(receiptExpiry),
                $"Receipt expiry must be greater than zero and no more than " +
                $"{MaximumGuardedConsumeReceiptLifetime.TotalMinutes} minutes.");
        }

        const string guardedConsumeWithReceiptScript = """
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

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                guardedConsumeWithReceiptScript,
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
            db);

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

    public async Task<RedisValue> StringReconcileConsumeReceiptAsync(
        string valueKey,
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry,
        int db = -1)
    {
        if (string.IsNullOrWhiteSpace(receiptKey))
            throw new ArgumentException("A receipt key is required.", nameof(receiptKey));
        if (string.IsNullOrWhiteSpace(consumeNonce) || consumeNonce.Length > 128)
            throw new ArgumentException("A bounded consume nonce is required.", nameof(consumeNonce));
        if (receiptExpiry <= TimeSpan.Zero || receiptExpiry > MaximumGuardedConsumeReceiptLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(receiptExpiry),
                $"Receipt expiry must be greater than zero and no more than " +
                $"{MaximumGuardedConsumeReceiptLifetime.TotalMinutes} minutes.");
        }

        const string reconcileConsumeReceiptScript = """
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

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                reconcileConsumeReceiptScript,
                [valueKey, receiptKey],
                [consumeNonce, checked((long)receiptExpiry.TotalMilliseconds)],
                CommandFlags.DemandMaster),
            db);

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

    public async Task<long> TryReserveGameServerMatchOwnerAsync(
        string nodeLeaseKey,
        string nodeAcceptingKey,
        string expectedGeneration,
        string nodeSlotsKey,
        string matchOwnerKey,
        string fenceKey,
        string nodeId,
        long matchingId,
        int capacity,
        TimeSpan ownerLifetime,
        int db = -1)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (ownerLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ownerLifetime));

        const string reserveOwnerScript =
            "if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end; " +
            "if redis.call('GET', KEYS[2]) ~= ARGV[1] then return 0 end; " +
            "if redis.call('EXISTS', KEYS[4]) ~= 0 then return 0 end; " +
            "local redisTime = redis.call('TIME'); " +
            "local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000); " +
            "redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now); " +
            "if redis.call('ZCARD', KEYS[3]) >= tonumber(ARGV[2]) then return 0 end; " +
            "local fence = redis.call('INCR', KEYS[5]); " +
            "local token = ARGV[3] .. '|' .. ARGV[1] .. '|' .. fence; " +
            "if not redis.call('SET', KEYS[4], token, 'PX', ARGV[5], 'NX') then return 0 end; " +
            "redis.call('ZADD', KEYS[3], now + tonumber(ARGV[5]), ARGV[4]); " +
            "local desiredTtl = tonumber(ARGV[5]) + 3600000; " +
            "local currentTtl = redis.call('PTTL', KEYS[3]); " +
            "if currentTtl < desiredTtl then redis.call('PEXPIRE', KEYS[3], desiredTtl) end; " +
            "return fence";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                reserveOwnerScript,
                [nodeLeaseKey, nodeAcceptingKey, nodeSlotsKey, matchOwnerKey, fenceKey],
                [
                    expectedGeneration,
                    capacity,
                    nodeId,
                    matchingId,
                    checked((long)ownerLifetime.TotalMilliseconds)
                ],
                CommandFlags.DemandMaster),
            db);
        return (long)result;
    }

    public async Task<bool> RenewGameServerMatchOwnerAsync(
        string matchOwnerKey,
        string expectedOwnerToken,
        string nodeSlotsKey,
        long matchingId,
        TimeSpan ownerLifetime,
        int db = -1)
    {
        if (ownerLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ownerLifetime));

        const string renewOwnerScript =
            "if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end; " +
            "local redisTime = redis.call('TIME'); " +
            "local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000); " +
            "redis.call('PEXPIRE', KEYS[1], ARGV[3]); " +
            "redis.call('ZADD', KEYS[2], now + tonumber(ARGV[3]), ARGV[2]); " +
            "local desiredTtl = tonumber(ARGV[3]) + 3600000; " +
            "local currentTtl = redis.call('PTTL', KEYS[2]); " +
            "if currentTtl < desiredTtl then redis.call('PEXPIRE', KEYS[2], desiredTtl) end; " +
            "return 1";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                renewOwnerScript,
                [matchOwnerKey, nodeSlotsKey],
                [
                    expectedOwnerToken,
                    matchingId,
                    checked((long)ownerLifetime.TotalMilliseconds)
                ],
                CommandFlags.DemandMaster),
            db);
        return (long)result == 1;
    }

    public async Task<bool> ReleaseGameServerMatchOwnerAsync(
        string matchOwnerKey,
        string expectedOwnerToken,
        string nodeSlotsKey,
        long matchingId,
        int db = -1)
    {
        const string releaseOwnerScript =
            "if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end; " +
            "redis.call('DEL', KEYS[1]); redis.call('ZREM', KEYS[2], ARGV[2]); return 1";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                releaseOwnerScript,
                [matchOwnerKey, nodeSlotsKey],
                [expectedOwnerToken, matchingId],
                CommandFlags.DemandMaster),
            db);
        return (long)result == 1;
    }

    public async Task<bool> ReleaseGameServerNodeLeaseAsync(
        string nodeLeaseKey,
        string nodeAcceptingKey,
        string nodeDescriptorKey,
        string nodeHeartbeatIndexKey,
        string nodeId,
        string expectedGeneration,
        int db = -1)
    {
        const string releaseNodeLeaseScript =
            "if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end; " +
            "if redis.call('GET', KEYS[2]) == ARGV[1] then redis.call('DEL', KEYS[2]) end; " +
            "redis.call('DEL', KEYS[3]); redis.call('DEL', KEYS[1]); " +
            "redis.call('ZREM', KEYS[4], ARGV[2]); return 1";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                releaseNodeLeaseScript,
                [nodeLeaseKey, nodeAcceptingKey, nodeDescriptorKey, nodeHeartbeatIndexKey],
                [expectedGeneration, nodeId],
                CommandFlags.DemandMaster),
            db);
        return (long)result == 1;
    }

    public async Task<bool> TryClaimSortedSetEntryAsync(
        string sortedSetKey,
        RedisValue entry,
        string claimKey,
        string claimValue,
        TimeSpan expiry,
        int db = -1
    )
    {
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be greater than zero.");

        const string claimEntryScript =
            "if redis.call('ZSCORE', KEYS[1], ARGV[1]) == false then return 0 end; " +
            "if redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[3], 'NX') then return 1 else return 0 end";

        RedisResult result = await ExecuteAtomicRedisCommandAsync(
            database => database.ScriptEvaluateAsync(
                claimEntryScript,
                [sortedSetKey, claimKey],
                [entry, claimValue, checked((long)expiry.TotalMilliseconds)],
                CommandFlags.DemandMaster
            ),
            db
        );
        return (long)result == 1;
    }

    public Task<bool> KeyDeleteAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.KeyDeleteAsync(key), db);
    }

    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.KeyExpireAsync(key, expiry), db);
    }

    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.SortedSetAddAsync(key, value, score), db);
    }

    public async Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity,
        double stop = double.PositiveInfinity, int db = -1)
    {
        var result = await ExecuteRedisCommandAsync(
            database => database.SortedSetRangeByScoreAsync(key, start, stop, order: Order.Ascending),
            db
        );
        return result.Select(rv => (byte[])rv!).ToArray();
    }

    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.SortedSetRemoveAsync(key, value), db);
    }

    private async Task<T> ExecuteRedisCommandAsync<T>(Func<IDatabase, Task<T>> action, int db = -1)
    {
        return await redisPool.ExecuteWithRetryAsync(action, db);
    }

    private async Task<T> ExecuteAtomicRedisCommandAsync<T>(Func<IDatabase, Task<T>> action, int db = -1)
    {
        // 결과를 잃은 요청을 자동 재실행하면 increment/consume이 중복 적용될 수 있다.
        return await redisPool.ExecuteWithRetryAsync(action, db, retryCount: 1);
    }
}
