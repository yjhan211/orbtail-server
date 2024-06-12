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

        public void HashSet(string key, string field, int value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.HashSet(key, field, value));
        }

        public void HashSet(string key, string field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.HashSet(key, field, value));
        }

        public void HashSet(string key, long field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.HashSet(key, field, value));
        }

        public RedisValue HashGet(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashGet(key, field, CommandFlags.PreferReplica));
        }

        public RedisValue HashGet(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashGet(key, field, CommandFlags.PreferReplica));
        }

        public RedisValue[]? HashGet(string key, RedisValue[] fields)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            var result = Task.Run(async () =>
                {
                    return await this.QueryRedis(
                        async (db) =>
                        {
                            var pipeline = db.CreateBatch();
                            var task = pipeline.HashGetAsync(
                                key,
                                fields,
                                CommandFlags.PreferReplica
                            );

                            pipeline.Execute();
                            return await task;
                        }
                    );
                })
                .GetAwaiter()
                .GetResult();

            return result;
        }

        public HashEntry[] HashGetAll(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashGetAll(key, CommandFlags.PreferReplica));
        }

        public bool HashDelete(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashDelete(key, field));
        }

        public bool HashDelete(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashDelete(key, field));
        }

        public bool HashExists(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.HashExists(key, field, CommandFlags.PreferReplica));
        }

        public RedisValue[] ListRange(string key, int start = 0, int end = -1)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis(
                (db) => db.ListRange(key, start, end, CommandFlags.PreferReplica)
            );
        }

        public RedisValue[] ListRange(List<string> keys, int start = 0, int end = -1)
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
                        key =>
                            this.QueryRedis(
                                db => db.ListRange(key, start, end, CommandFlags.PreferReplica)
                            )
                    );

                // var batch_results = await Task.WhenAll(batch_tasks);
                results.AddRange(batch_tasks.SelectMany(r => r));
            }

            return results.ToArray();
        }

        public List<(string key, RedisValue value)> ListRangeWithKey(
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
                var batch_results = keys.Skip(i)
                    .Take(Config.BATCH_SIZE)
                    .ToList()
                    .Select(key =>
                    {
                        var range = this.QueryRedis(
                            db => db.ListRange(key, start, end, CommandFlags.PreferReplica)
                        );
                        return range.Select(value => (key, value));
                    });

                results.AddRange(batch_results.SelectMany(x => x));
            }

            return results;
        }

        public void ListPush(string key, string value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.ListRightPush(key, value));
        }

        public void ListRemove(string key, string value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.ListRemove(key, value));
        }

        public void Enqueue(string key, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.ListRightPush(key, value));
        }

        public byte[]? Dequeue(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.ListLeftPop(key));
        }

        public long ListLength(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.ListLength(key));
        }

        public long StringIncrement(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return this.QueryRedis((db) => db.StringIncrement(key));
        }

        public void KeyDelete(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            this.QueryRedis((db) => db.KeyDelete(key));
        }

        public T QueryRedis<T>(Func<IDatabase, T> action)
        {
            try
            {
                var database = this.conn.GetDatabase();
                return action(database);
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
