using StackExchange.Redis;

namespace network.infrastructure.redis;

/// <summary>
///     Redis의 Hash, String, Sorted Set에 대한 공통 저수준 연산을 제공한다.
///     조건부 변경이나 여러 명령을 함께 처리해야 하는 작업은 Lua 스크립트로 원자적으로 실행한다.
/// </summary>
public class RedisOperations(RedisConnection redisConnection) : IRedisOperations
{
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

        await ExecuteRedisCommandAsync(
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

        await ExecuteRedisCommandAsync(
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


    public Task<HashEntry[]> HashGetAllAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAllAsync(key, CommandFlags.DemandMaster), db);
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

    public Task<long> StringIncrementAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.StringIncrementAsync(key), db);
    }

    public Task<long> StringIncrementByAsync(string key, long increment, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.StringIncrementAsync(key, increment), db);
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
        return ExecuteRedisCommandAsync(
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
        return ExecuteRedisCommandAsync(
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

        var result = await ExecuteRedisCommandAsync(
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

    public async Task<bool> StringSetIfNewerGenerationAsync(string key, string newValue, TimeSpan expiry, int db = -1)
    {
        ArgumentNullException.ThrowIfNull(newValue);
        if (expiry.TotalMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(expiry));

        // Lua 숫자는 큰 int64를 정확히 표현하지 못하므로 자릿수와 문자열 순서로 비교한다.
        const string script = """
            local function generation(value)
                local n = string.match(value, '^([0-9]+)|')
                if n then n = string.gsub(n, '^0+', '') end
                if not n or n == '' or #n > 19 or (#n == 19 and n > '9223372036854775807') then
                    error('Invalid generation-prefixed value')
                end
                return n
            end
            local proposed = generation(ARGV[1])
            local current = redis.call('GET', KEYS[1])
            if current and current ~= '' then
                local existing = generation(current)
                if #existing > #proposed or (#existing == #proposed and existing >= proposed) then return 0 end
            end
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
            return 1
            """;
        var result = await ExecuteRedisCommandAsync(
            database => database.ScriptEvaluateAsync(script, [key],
                [newValue, checked((long)expiry.TotalMilliseconds)], CommandFlags.DemandMaster), db);
        return (long)result == 1;
    }

    public async Task<bool> StringDeleteIfEqualsAsync(string key, string expectedValue, int db = -1)
    {
        ArgumentNullException.ThrowIfNull(expectedValue);

        const string deleteIfEqualsScript =
            "if redis.call('GET', KEYS[1]) == ARGV[1] then " +
            "return redis.call('DEL', KEYS[1]) else return 0 end";

        var result = await ExecuteRedisCommandAsync(
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

    public Task<bool> KeyDeleteAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.KeyDeleteAsync(key), db);
    }

    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.KeyExpireAsync(key, expiry), db);
    }

    public async Task<bool> StringSetIfQueueEntryExistsAsync(string queueKey, string detailsKey, string member,
        string key, string value, TimeSpan expiry, int db = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        if (expiry < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be at least one millisecond.");
        const string script = """
            if redis.call('ZSCORE', KEYS[1], ARGV[1]) == false then return 0 end
            if redis.call('HEXISTS', KEYS[2], ARGV[1]) == 0 then return 0 end
            if redis.call('SET', KEYS[3], ARGV[2], 'PX', ARGV[3], 'NX') then return 1 end
            return 0
            """;
        var result = await ExecuteRedisCommandAsync(database => database.ScriptEvaluateAsync(
            script, [queueKey, detailsKey, key], [member, value, checked((long)expiry.TotalMilliseconds)],
            CommandFlags.DemandMaster), db);
        return (long)result == 1;
    }

    /// <summary>새 식별자와 상세 데이터를 함께 등록한다. 기존 식별자는 덮어쓰지 않는다.</summary>
    public async Task<bool> SortedSetAddWithHashAsync(string key, string hashKey, string member, byte[] data, double score, int db = -1)
    {
        if (!double.IsFinite(score)) throw new ArgumentOutOfRangeException(nameof(score));
        const string script = """
            local score = redis.call('ZSCORE', KEYS[1], ARGV[1])
            local exists = redis.call('HEXISTS', KEYS[2], ARGV[1])
            if score or exists == 1 then return 0 end
            redis.call('HSET', KEYS[2], ARGV[1], ARGV[2])
            redis.call('ZADD', KEYS[1], ARGV[3], ARGV[1])
            return 1
            """;
        var result = await ExecuteRedisCommandAsync(database => database.ScriptEvaluateAsync(
            script, [key, hashKey], [member, data, score], CommandFlags.DemandMaster), db);
        return (long)result == 1;
    }

    /// <summary>대기열 항목과 같은 식별자의 상세 데이터를 함께 삭제한다.</summary>
    public async Task<bool> SortedSetRemoveWithHashAsync(string key, string hashKey, byte[] member, int db = -1)
    {
        const string script = """
            redis.call('HEXISTS', KEYS[2], ARGV[1])
            local removed = redis.call('ZREM', KEYS[1], ARGV[1])
            redis.call('HDEL', KEYS[2], ARGV[1])
            return removed
            """;
        var result = await ExecuteRedisCommandAsync(database => database.ScriptEvaluateAsync(
            script, [key, hashKey], [member], CommandFlags.DemandMaster), db);
        return (long)result == 1;
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

    private Task<T> ExecuteRedisCommandAsync<T>(Func<IDatabase, Task<T>> action, int db = -1)
    {
        return action(redisConnection.GetDatabase(db));
    }
}
