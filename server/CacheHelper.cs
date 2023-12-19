using StackExchange.Redis;

namespace game_server
{
    public static class CacheHelper
    {
#pragma warning disable CS8618
        static RedisConnection conn;
#pragma warning restore

        public static void Initialize(RedisConnection conn)
        {
            CacheHelper.conn = conn;
            Console.WriteLine("CacheHelper Initialize success");
        }

        public static Transaction BeginTransaction()
        {
            if (conn == null)
            {
                throw new Exception("RedisHelper.conn is null");
            }

            return new Transaction(conn._database.CreateTransaction());
        }

        public static async Task HashSet(string key, string field, string value)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashSet Fail. err: {e.Message}");
            }
        }

        public static async Task HashSet(string key, long field, string value)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.HashSetAsync(key, field, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashSet Fail. err: {e.Message}");
            }
        }

        public static async Task<RedisValue> HashGet(string key, string field)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashGet Fail. err: {e.Message}");
                return RedisValue.Null;
            }
        }

        public static async Task<RedisValue> HashGet(string key, long field)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await conn.BasicRetryAsync((db) => db.HashGetAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashGet Fail. err: {e.Message}");
                return RedisValue.Null;
            }
        }

        public static async Task<RedisValue[]?> HashGet(string key, RedisValue[] fields)
        {
            try
            {
                if (conn == null)
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

        public static async Task HashDelete(string key, string field)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public static async Task HashDelete(string key, long field)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.HashDeleteAsync(key, field));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public static async Task<RedisValue[]> ListRange(string key, int start = 0, int end = -1)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await conn.BasicRetryAsync((db) => db.ListRangeAsync(key, start, end));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
                return Array.Empty<RedisValue>();
            }
        }

        // public static async Task<RedisValue[]> ListRange(
        //     List<string> keys,
        //     int start = 0,
        //     int end = -1
        // )
        // {
        //     try
        //     {
        //         if (conn == null)
        //         {
        //             throw new Exception("RedisHelper.conn is null");
        //         }

        //         var tasks = keys.Select(
        //             async key =>
        //                 await conn.BasicRetryAsync(db => db.ListRangeAsync(key, start, end))
        //         );

        //         var results = await Task.WhenAll(tasks);
        //         return results.SelectMany(r => r).ToArray();
        //     }
        //     catch (Exception e)
        //     {
        //         Console.WriteLine($"ListRangeMultipleKeys Fail. err: {e.Message}");
        //         return Array.Empty<RedisValue>();
        //     }
        // }

        public static async Task<RedisValue[]> ListRange(
            List<string> keys,
            int start = 0,
            int end = -1
        )
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

        public static async Task ListPush(string key, string value)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.ListRightPushAsync(key, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public static async Task ListRemove(string key, string value)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                await conn.BasicRetryAsync((db) => db.ListRemoveAsync(key, value));
            }
            catch (Exception e)
            {
                Console.WriteLine($"HashDelete Fail. err: {e.Message}");
            }
        }

        public static async Task<long> StringIncrement(string key)
        {
            try
            {
                if (conn == null)
                {
                    throw new Exception("RedisHelper.conn is null");
                }

                return await conn.BasicRetryAsync((db) => db.StringIncrementAsync(key));
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

        public void HashSet(string key, string field, string value)
        {
            transaction.HashSetAsync(key, field, value);
        }

        public void HashSet(string key, long field, string value)
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
