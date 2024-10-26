using System.Collections.Concurrent;
using StackExchange.Redis;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;

namespace network.infrastructure
{
    public class RedisConnectionPool
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<int, IDatabase> _databases = new();
        private ConfigurationOptions? _options;
        private Lazy<ConnectionMultiplexer>? _lazyConnection;

        public void Initialize(string connectionString)
        {
            _options = ConfigurationOptions.Parse(connectionString);
            _options.AbortOnConnectFail = false;
            _options.ConnectTimeout = 5000;
            _options.SyncTimeout = 5000;
            _options.ConnectRetry = 3;

            _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
            {
                try
                {
                    return ConnectionMultiplexer.Connect(_options);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to connect to Redis: {ex.Message}", ex);
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ConnectionMultiplexer GetConnection()
        {
            if (_lazyConnection == null)
            {
                throw new InvalidOperationException("Redis connection is not initialized.");
            }

            return _lazyConnection.Value;
        }

        public IDatabase GetDatabase(int db = -1)
        {
            return _databases.GetOrAdd(db, dbNum => _lazyConnection!.Value.GetDatabase(dbNum));
        }

        public Task<IDatabase> GetDatabaseAsync(int db = -1)
        {
            return Task.FromResult(GetDatabase(db));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_lazyConnection!.IsValueCreated)
                {
                    _lazyConnection.Value.Dispose();
                }
            }
        }

        public RedLockFactory GetRedLockFactory()
        {
            var connection = GetConnection();
            var endpoints = connection
                .GetEndPoints()
                .Select(endpoint => new RedLockEndPoint { EndPoint = endpoint })
                .ToList();

            return RedLockFactory.Create(endpoints);
        }

        public async Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3)
        {
            var delay = 100;  // 시작 딜레이

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

                    await Task.Delay(delay);
                    delay *= 2;  // 지수 백오프
                }
                catch (RedisConnectionException)
                {
                    if (i == retryCount - 1)
                        throw;

                    await Task.Delay(delay);
                    delay *= 2;  // 지수 백오프
                }
            }
            throw new Exception($"Redis operation failed after {retryCount} retries");
        }
    }
}