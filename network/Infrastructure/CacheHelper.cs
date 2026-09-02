using network.common;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure;

/// <summary>
///     Provides reusable Redis data-structure and compare-and-set primitives used by server application services.
///     Domain-specific multi-key state transitions belong to their owning Redis stores.
/// </summary>
public class CacheHelper(IRedisConnection redisPool) : ICacheHelper
{
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
