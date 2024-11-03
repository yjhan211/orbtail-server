using System.Collections.Concurrent;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;

namespace network.infrastructure;

public class RedisConnectionPool
{
    private readonly ConcurrentDictionary<int, IDatabase> _databases = new();
    private readonly object _lock = new();
    private Lazy<ConnectionMultiplexer>? _lazyConnection;
    private ConfigurationOptions? _options;

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

    private ConnectionMultiplexer GetConnection()
    {
        if (_lazyConnection == null) throw new InvalidOperationException("Redis connection is not initialized.");

        return _lazyConnection.Value;
    }

    private IDatabase GetDatabase(int db = -1)
    {
        return _databases.GetOrAdd(db, dbNum => _lazyConnection!.Value.GetDatabase(dbNum));
    }

    private Task<IDatabase> GetDatabaseAsync(int db = -1)
    {
        return Task.FromResult(GetDatabase(db));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_lazyConnection!.IsValueCreated) _lazyConnection.Value.Dispose();
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
        var delay = 100; // 시작 딜레이

        for (var i = 0; i < retryCount; i++)
            try
            {
                var database = await GetDatabaseAsync(db);
                return await action(database);
            }
            catch (RedisTimeoutException)
            {
                if (i == retryCount - 1)
                    throw;

                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }
            catch (RedisConnectionException)
            {
                if (i == retryCount - 1)
                    throw;

                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }

        throw new Exception($"Redis operation failed after {retryCount} retries");
    }
}