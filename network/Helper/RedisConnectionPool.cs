using StackExchange.Redis;
using StackExchange.Redis.MultiplexerPool;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using System.Collections.Concurrent;

namespace network
{
    public static class RedisConnectionPool
    {
        private static Lazy<ConnectionMultiplexer>? _lazyConnection;
        private static readonly object _lock = new object();
        private static readonly ConcurrentDictionary<int, IDatabase> _databases =
            new ConcurrentDictionary<int, IDatabase>();
        private static readonly ConfigurationOptions _options = new ConfigurationOptions();

        public static void Initialize(string connectionString)
        {
            _options.EndPoints.Add(connectionString);
            _options.AbortOnConnectFail = false;
            _options.ConnectRetry = 5;
            _options.ConnectTimeout = 5000;
            _options.SyncTimeout = 5000;
            _options.AllowAdmin = true;
            _options.Ssl = true; // 필요한 경우

            _lazyConnection = new Lazy<ConnectionMultiplexer>(
                () => ConnectionMultiplexer.Connect(_options)
            );
        }

        public static ConnectionMultiplexer GetConnection()
        {
            if (!_lazyConnection!.Value.IsConnected)
            {
                lock (_lock)
                {
                    if (!_lazyConnection.Value.IsConnected)
                    {
                        _lazyConnection.Value.Dispose();
                        _databases.Clear();
                        _lazyConnection = new Lazy<ConnectionMultiplexer>(
                            () => ConnectionMultiplexer.Connect(_options)
                        );
                    }
                }
            }
            return _lazyConnection.Value;
        }

        public static IDatabase GetDatabase(int db = -1)
        {
            return _databases.GetOrAdd(db, dbNum => _lazyConnection!.Value.GetDatabase(dbNum));
        }

        public static Task<IDatabase> GetDatabaseAsync(int db = -1)
        {
            return Task.FromResult(GetDatabase(db));
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                if (_lazyConnection!.IsValueCreated)
                {
                    _lazyConnection.Value.Dispose();
                }
            }
        }

        public static RedLockFactory GetRedLockFactory()
        {
            var connection = GetConnection();
            var endpoints = connection
                .GetEndPoints()
                .Select(endpoint => new RedLockEndPoint { EndPoint = endpoint })
                .ToList();

            return RedLockFactory.Create(endpoints);
        }

        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<IDatabase, Task<T>> action,
            int db = -1,
            int retryCount = 3
        )
        {
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    var database = GetDatabase(db);
                    return await action(database);
                }
                catch (RedisTimeoutException)
                {
                    if (i == retryCount - 1)
                        throw;
                    await Task.Delay(100 * (i + 1)); // 지수 백오프
                }
            }
            throw new Exception("Redis operation failed after retries");
        }
    }
}
