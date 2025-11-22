using System.Collections.Concurrent;
using network.interfaces;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;

namespace network.infrastructure;

public class RedisConnectionPool : IRedisConnectionPool
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
            if (_lazyConnection != null && _lazyConnection.IsValueCreated)
                _lazyConnection.Value.Dispose();
        }
    }

    public IRedLockFactory GetRedLockFactory()
    {
        var connection = GetConnection();
        var endpoints = connection
            .GetEndPoints()
            .Select(endpoint => new RedLockEndPoint { EndPoint = endpoint })
            .ToList();

        // 여기서는 직접 RedLockFactory를 반환하지만,
        // 실제 구현에서는 IRedLockFactory를 구현한 어댑터 클래스를 반환해야 함
        return new RedLockFactoryAdapter(RedLockFactory.Create(endpoints));
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3)
    {
        var delay = 100; // 시작 딜레이 (ms)

        for (var i = 0; i < retryCount; i++)
        {
            try
            {
                var database = await GetDatabaseAsync(db);
                return await action(database);
            }
            catch (RedisTimeoutException ex)
            {
                if (i == retryCount - 1)
                {
                    Console.WriteLine($"Redis timeout after {retryCount} retries: {ex.Message}");
                    throw;
                }

                Console.WriteLine($"Redis timeout (attempt {i + 1}/{retryCount}), retrying in {delay}ms...");
                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }
            catch (RedisConnectionException ex)
            {
                if (i == retryCount - 1)
                {
                    Console.WriteLine($"Redis connection error after {retryCount} retries: {ex.Message}");
                    throw;
                }

                Console.WriteLine($"Redis connection error (attempt {i + 1}/{retryCount}), retrying in {delay}ms...");
                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }
        }

        throw new Exception($"Redis operation failed after {retryCount} retries");
    }
}

// RedLockFactory의 어댑터 클래스
public class RedLockFactoryAdapter(RedLockFactory redLockFactory) : IRedLockFactory
{
    // CreateLockAsync 구현
    public Task<IRedLock> CreateLockAsync(string resource, TimeSpan expiryTime)
    {
        return redLockFactory.CreateLockAsync(resource, expiryTime);
    }

    // 필요한 경우 다른 메서드들도 구현
}