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
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashSet Fail. err: {e.Message}");
            }
        }

        public async Task HashSet(string key, long field, byte[] value)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashSet Fail. err: {e.Message}");
            }
        }

        public async Task<RedisValue> HashGet(string key, string field)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await this.conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashGet Fail. err: {e.Message}");
                return RedisValue.Null;
            }
        }

        public async Task<RedisValue> HashGet(string key, long field)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await this.conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashGet Fail. err: {e.Message}");
                return RedisValue.Null;
            }
        }

        public async Task<RedisValue[]?> HashGet(string key, RedisValue[] fields)
        {
            try
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
            catch (Exception e)
            {
                Console.WriteLine($"HashGetBatch Fail. err: {e.Message}");
                return null;
            }
        }

        public async Task HashDelete(string key, string field)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public async Task HashDelete(string key, long field)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public async Task<RedisValue[]> ListRange(string key, int start = 0, int end = -1)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await this.conn.BasicRetryAsync((db) => db.ListRangeAsync(key, start, end));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
                return Array.Empty<RedisValue>();
            }
        }

        public async Task<RedisValue[]> ListRange(List<string> keys, int start = 0, int end = -1)
        {
            const int batch_size = 10;

            if (keys == null || keys.Count == 0)
            {
                return Array.Empty<RedisValue>();
            }

            var results = new List<RedisValue>();

            for (int i = 0; i < keys.Count; i += batch_size)
            {
                var batch_keys = keys.Skip(i).Take(batch_size).ToList();

                try
                {
                    var batchTasks = batch_keys.Select(
                        async key =>
                            await conn.BasicRetryAsync(db => db.ListRangeAsync(key, start, end))
                    );

                    var batchResults = await Task.WhenAll(batchTasks);
                    results.AddRange(batchResults.SelectMany(r => r));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"ListRangeBatch error: {e.Message}");
                    // 예외 처리 - 로깅 또는 재시도 등
                }
            }

            return results.ToArray();
        }

        public async Task<List<(string key, RedisValue value)>> ListRangeWithKey(
            List<string> keys,
            int start = 0,
            int end = -1
        )
        {
            const int batch_size = 10;

            if (keys == null || keys.Count == 0)
            {
                return new List<(string key, RedisValue value)>();
            }

            var results = new List<(string key, RedisValue value)>();

            for (int i = 0; i < keys.Count; i += batch_size)
            {
                var batch_keys = keys.Skip(i).Take(batch_size).ToList();

                try
                {
                    var batchTasks = batch_keys.Select(async key =>
                    {
                        var range = await conn.BasicRetryAsync(
                            db => db.ListRangeAsync(key, start, end)
                        );
                        return range.Select(value => (key, value));
                    });

                    var batchResults = await Task.WhenAll(batchTasks);
                    results.AddRange(batchResults.SelectMany(x => x));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"ListRangeBatch error: {e.Message}");
                    // 예외 처리 - 로깅 또는 재시도 등
                }
            }

            return results;
        }

        public async Task ListPush(string key, string value)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.ListRightPushAsync(key, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"ListPush Fail. err: {e.Message}");
            }
        }

        public async Task ListRemove(string key, string value)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.ListRemoveAsync(key, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"ListRemove Fail. err: {e.Message}");
            }
        }

        public async Task Enqueue(string key, byte[] value)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await this.conn.BasicRetryAsync((db) => db.ListLeftPushAsync(key, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"ListPush Fail. err: {e.Message}");
            }
        }

        public async Task<byte[]?> Dequeue(string key)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await this.conn.BasicRetryAsync((db) => db.ListRightPopAsync(key));
            }
            catch (Exception e)
            {
                Console.WriteLine($"ListPush Fail. err: {e.Message}");
                return null;
            }
        }

        public async Task<long> StringIncrement(string key)
        {
            try
            {
                if (this.conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await this.conn.BasicRetryAsync((db) => db.StringIncrementAsync(key));
            }
            catch (Exception e)
            {
                Console.WriteLine($"StringIncrement Fail. err: {e.Message}");
                return -1;
            }
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
                Console.WriteLine($"Transaction execution failed. err: {e.Message}");
                return false;
            }
        }
    }
}
