using StackExchange.Redis;

namespace game_server
{
    public static class RedisHelper
    {
        static RedisConnection? conn;

        public static void Initialize(RedisConnection conn)
        {
            RedisHelper.conn = conn;
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

        public static async Task<RedisValue[]?> HashGet(string key, List<string> field_list)
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
                        var task = pipeline.HashGetAsync(
                            key,
                            field_list.Select(field => (RedisValue)field).ToArray()
                        );

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
}
