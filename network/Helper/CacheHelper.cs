using StackExchange.Redis;

namespace network
{
    public class CacheHelper
    {
        public ConnectionMultiplexer conn;

        public CacheHelper(ConnectionMultiplexer conn)
        {
            this.conn = conn;
        }

        public async Task HashSet(string key, string field, int value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.HashSetAsync(key, field, value));
        }

        public async Task HashSet(string key, string field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.HashSetAsync(key, field, value));
        }

        public async Task HashSet(string key, long field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.HashSetAsync(key, field, value));
        }

        public async Task<RedisValue> HashGet(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync(
                (db) => db.HashGetAsync(key, field, CommandFlags.PreferReplica)
            );
        }

        public async Task<RedisValue> HashGet(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync(
                (db) => db.HashGetAsync(key, field, CommandFlags.PreferReplica)
            );
        }

        public async Task<RedisValue[]?> HashGet(string key, RedisValue[] fields)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            var result = await this.QueryRedisAsync(
                async (db) =>
                {
                    var pipeline = db.CreateBatch();
                    var task = pipeline.HashGetAsync(key, fields, CommandFlags.PreferReplica);

                    pipeline.Execute();
                    return await task;
                }
            );

            return result;
        }

        public async Task<HashEntry[]?> HashGetAll(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync(
                (db) => db.HashGetAllAsync(key, CommandFlags.PreferReplica)
            );
        }

        public async Task HashDelete(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.HashDeleteAsync(key, field));
        }

        public async Task HashDelete(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.HashDeleteAsync(key, field));
        }

        public async Task<bool> HashExists(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync(
                (db) => db.HashExistsAsync(key, field, CommandFlags.PreferReplica)
            );
        }

        public async Task<RedisValue[]> ListRange(string key, int start = 0, int end = -1)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync(
                (db) => db.ListRangeAsync(key, start, end, CommandFlags.PreferReplica)
            );
        }

        public async Task<RedisValue[]> ListRange(List<string> keys, int start = 0, int end = -1)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            if (keys == null || keys.Count == 0)
            {
                return Array.Empty<RedisValue>();
            }

            var results = new List<RedisValue>();

            for (int i = 0; i < keys.Count; i += Config.BATCH_SIZE)
            {
                var batch_tasks = keys.Skip(i)
                    .Take(Config.BATCH_SIZE)
                    .ToList()
                    .Select(
                        async key =>
                            await this.QueryRedisAsync(
                                db => db.ListRangeAsync(key, start, end, CommandFlags.PreferReplica)
                            )
                    );

                var batch_results = await Task.WhenAll(batch_tasks);
                results.AddRange(batch_results.SelectMany(r => r));
            }

            return results.ToArray();
        }

        public async Task<List<(string key, RedisValue value)>> ListRangeWithKey(
            List<string> keys,
            int start = 0,
            int end = -1
        )
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            if (keys == null || keys.Count == 0)
            {
                return new List<(string key, RedisValue value)>();
            }

            var results = new List<(string key, RedisValue value)>();

            for (int i = 0; i < keys.Count; i += Config.BATCH_SIZE)
            {
                var batch_tasks = keys.Skip(i)
                    .Take(Config.BATCH_SIZE)
                    .ToList()
                    .Select(async key =>
                    {
                        var range = await this.QueryRedisAsync(
                            db => db.ListRangeAsync(key, start, end, CommandFlags.PreferReplica)
                        );
                        return range.Select(value => (key, value));
                    });

                var batchResults = await Task.WhenAll(batch_tasks);
                results.AddRange(batchResults.SelectMany(x => x));
            }

            return results;
        }

        public async Task ListPush(string key, string value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.ListRightPushAsync(key, value));
        }

        public async Task ListRemove(string key, string value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.ListRemoveAsync(key, value));
        }

        public async Task Enqueue(string key, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.ListLeftPushAsync(key, value));
        }

        public async Task<byte[]?> Dequeue(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync((db) => db.ListRightPopAsync(key));
        }

        public async Task<long> ListLength(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync((db) => db.ListLengthAsync(key));
        }

        public async Task<long> StringIncrement(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.QueryRedisAsync((db) => db.StringIncrementAsync(key));
        }

        public async Task KeyDelete(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.QueryRedisAsync((db) => db.KeyDeleteAsync(key));
        }

        public async Task<T> QueryRedisAsync<T>(Func<IDatabase, Task<T>> action)
        {
            try
            {
                var database = this.conn.GetDatabase();
                return await action(database);
            }
            catch (Exception ex)
            {
                // 예외 처리 로직 추가
                // 예외 로깅, 재시도 로직 등 구현 가능
                throw new Exception($"Redis query failed: {ex.Message}", ex);
            }
            finally
            {
                // 필요한 경우 정리 작업 수행
            }
        }
    }
}
