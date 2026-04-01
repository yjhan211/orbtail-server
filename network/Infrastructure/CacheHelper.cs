using network.common;
using network.infrastructure;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure;

public class CacheHelper(IRedisConnectionPool redisPool) : ICacheHelper
{
    public IRedLockFactory GetRedLockFactory() => redisPool.GetRedLockFactory();

    private async Task<T> ExecuteRedisCommandAsync<T>(Func<IDatabase, Task<T>> action, int db = -1)
    {
        return await redisPool.ExecuteWithRetryAsync(action, db);
    }

    public Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashSetAsync(key, field, value), db);
    }

    public Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashSetAsync(key, field, value), db);
    }

    public Task<RedisValue> HashGetAsync(string key, string field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.PreferReplica), db);
    }

    public Task<RedisValue> HashGetAsync(string key, long field, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.HashGetAsync(key, field, CommandFlags.PreferReplica), db);
    }

    public Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1)
    {
        return ExecuteRedisCommandAsync(
            async database =>
            {
                var batch = database.CreateBatch();
                var task = batch.HashGetAsync(key, fields, CommandFlags.PreferReplica);
                batch.Execute();
                return await task;
            },
            db
        );
    }

    public Task<HashEntry[]> HashGetAllAsync(string key, int db = -1)
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
            database => database.ListRangeAsync(key, start, end, CommandFlags.PreferReplica), db);
    }

    public async Task<RedisValue[]> ListRangeAsync(List<string> keys, int start = 0, int end = -1, int db = -1)
    {
        if (keys.Count == 0) return Array.Empty<RedisValue>();

        var results = new List<RedisValue>();

        for (var i = 0; i < keys.Count; i += Config.BATCH_SIZE)
        {
            var batchTasks = keys.Skip(i)
                .Take(Config.BATCH_SIZE)
                .Select(
                    key =>
                        ExecuteRedisCommandAsync(
                            database =>
                                database.ListRangeAsync(
                                    key,
                                    start,
                                    end,
                                    CommandFlags.PreferReplica
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
        for (var i = 0; i < keys.Count; i += Config.BATCH_SIZE)
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
                                CommandFlags.PreferReplica
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
        return ExecuteRedisCommandAsync(database => database.StringIncrementAsync(key), db);
    }

    public Task<bool> KeyDeleteAsync(string key, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.KeyDeleteAsync(key), db);
    }

    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.SortedSetAddAsync(key, value, score), db);
    }

    public async Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, int db = -1)
    {
        var result = await ExecuteRedisCommandAsync(
            database => database.SortedSetRangeByScoreAsync(key, start, stop, order: Order.Ascending, flags: CommandFlags.PreferReplica),
            db
        );
        return result.Select(rv => (byte[])rv!).ToArray();
    }

    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1)
    {
        return ExecuteRedisCommandAsync(database => database.SortedSetRemoveAsync(key, value), db);
    }
}
