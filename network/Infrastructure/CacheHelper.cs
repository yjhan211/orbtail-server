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
