using StackExchange.Redis;

namespace network
{
    public class CacheHelper
    {
        RedisConnection conn;

        public CacheHelper(RedisConnection conn)
        {
            this.conn = conn;
        }

        // public Transaction BeginTransaction()
        // {
        //     if (this.conn == null)
        //     {
        //         throw new Exception("RedisHelper.conn is null");
        //     }

        //     return new Transaction(this.conn._database.CreateTransaction());
        // }

        public async Task HashSet(string key, string field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
        }

        public async Task HashSet(string key, long field, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
        }

        public async Task<RedisValue> HashGet(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
        }

        public async Task<RedisValue> HashGet(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
        }

        public async Task<RedisValue[]?> HashGet(string key, RedisValue[] fields)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            var result = await conn.BasicRetryAsync(
                async (db) =>
                {
                    var pipeline = db.CreateBatch();
                    var task = pipeline.HashGetAsync(key, fields);

                    pipeline.Execute();
                    return await task;
                }
            );

            return result;
        }

        public async Task HashDelete(string key, string field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
        }

        public async Task HashDelete(string key, long field)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
        }

        public async Task<RedisValue[]> ListRange(string key, int start = 0, int end = -1)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.conn.BasicRetryAsync((db) => db.ListRangeAsync(key, start, end));
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
                            await conn.BasicRetryAsync(db => db.ListRangeAsync(key, start, end))
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
                        var range = await conn.BasicRetryAsync(
                            db => db.ListRangeAsync(key, start, end)
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

            await this.conn.BasicRetryAsync((db) => db.ListRightPushAsync(key, value));
        }

        public async Task ListRemove(string key, string value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.ListRemoveAsync(key, value));
        }

        public async Task Enqueue(string key, byte[] value)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            await this.conn.BasicRetryAsync((db) => db.ListLeftPushAsync(key, value));
        }

        public async Task<byte[]?> Dequeue(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.conn.BasicRetryAsync((db) => db.ListRightPopAsync(key));
        }

        public async Task<long> StringIncrement(string key)
        {
            if (this.conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return await this.conn.BasicRetryAsync((db) => db.StringIncrementAsync(key));
        }
    }

    public class Transaction
    {
        private readonly ITransaction transaction;

        public Transaction(ITransaction transaction)
        {
            this.transaction = transaction;
        }

        public void HashSet(string key, string field, byte[] value)
        {
            transaction.HashSetAsync(key, field, value);
        }

        public void HashSet(string key, long field, byte[] value)
        {
            transaction.HashSetAsync(key, field, value);
        }

        public void HashDelete(string key, string field)
        {
            transaction.HashDeleteAsync(key, field);
        }

        public void HashDelete(string key, long field)
        {
            transaction.HashDeleteAsync(key, field);
        }

        public void StringIncrement(string key)
        {
            transaction.StringIncrementAsync(key);
        }

        public async Task<bool> Execute()
        {
            try
            {
                await transaction.ExecuteAsync();
                return true;
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                return false;
            }
        }
    }
}
