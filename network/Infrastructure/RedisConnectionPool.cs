using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.interfaces;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;

namespace network.infrastructure;

public class RedisConnectionPool(ILogger<RedisConnectionPool> logger) : IRedisConnectionPool
{
    private readonly ConcurrentDictionary<int, IDatabase> _databases = new();
    private readonly object _lock = new();
    private Lazy<ConnectionMultiplexer>? _lazyConnection;
    private RedLockFactoryAdapter? _redLockFactory;
    private ConfigurationOptions? _options;
    private bool _disposed;

    public void Initialize(string connectionString)
    {
        Initialize(RedisConfigurationParser.ParseConnectionString(connectionString));
    }

    public void Initialize(RedisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_lazyConnection != null)
            {
                throw new InvalidOperationException("Redis connection is already initialized.");
            }

            _options = configuration.CreateClientOptions();
            _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
            {
                try
                {
                    var connection = ConnectionMultiplexer.Connect(_options);
                    try
                    {
                        RejectUnsupportedClusterTopology(connection);
                        return connection;
                    }
                    catch
                    {
                        connection.Dispose();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Failed to connect to Redis.", ex);
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public void Dispose()
    {
        RedLockFactoryAdapter? redLockFactory;
        ConnectionMultiplexer? connection = null;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            redLockFactory = _redLockFactory;
            _redLockFactory = null;
            if (_lazyConnection is { IsValueCreated: true })
                connection = _lazyConnection.Value;

            _lazyConnection = null;
            _options = null;
            _databases.Clear();
        }

        redLockFactory?.Dispose();
        connection?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public IRedLockFactory GetRedLockFactory()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_redLockFactory != null) return _redLockFactory;

            var multiplexer = new RedLockMultiplexer(GetConnection());
            _redLockFactory = new RedLockFactoryAdapter(
                RedLockFactory.Create(new List<RedLockMultiplexer> { multiplexer }));
            return _redLockFactory;
        }
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3)
    {
        int delay = 100; // 시작 딜레이 (ms)

        for (int i = 0; i < retryCount; i++)
            try
            {
                var database = await GetDatabaseAsync(db);
                return await action(database);
            }
            catch (RedisTimeoutException ex)
            {
                if (i == retryCount - 1)
                {
                    logger.LogWarning("Redis timeout after {RetryCount} retries: {Message}", retryCount, ex.Message);
                    throw;
                }

                logger.LogWarning("Redis timeout (attempt {Attempt}/{RetryCount}), retrying in {Delay}ms...", i + 1,
                    retryCount, delay);
                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }
            catch (RedisConnectionException ex)
            {
                if (i == retryCount - 1)
                {
                    logger.LogError("Redis connection error after {RetryCount} retries: {Message}", retryCount,
                        ex.Message);
                    throw;
                }

                logger.LogWarning("Redis connection error (attempt {Attempt}/{RetryCount}), retrying in {Delay}ms...",
                    i + 1, retryCount, delay);
                await Task.Delay(delay);
                delay *= 2; // 지수 백오프
            }

        throw new Exception($"Redis operation failed after {retryCount} retries");
    }

    private ConnectionMultiplexer GetConnection()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_lazyConnection == null) throw new InvalidOperationException("Redis connection is not initialized.");
            return _lazyConnection.Value;
        }
    }

    private IDatabase GetDatabase(int db = -1)
    {
        return _databases.GetOrAdd(db, dbNum => GetConnection().GetDatabase(dbNum));
    }

    private Task<IDatabase> GetDatabaseAsync(int db = -1)
    {
        return Task.FromResult(GetDatabase(db));
    }

    private static void RejectUnsupportedClusterTopology(ConnectionMultiplexer connection)
    {
        bool hasClusterNode = connection.GetEndPoints(configuredOnly: false)
            .Select(endpoint => connection.GetServer(endpoint))
            .Any(server => server.ServerType == ServerType.Cluster);
        if (hasClusterNode)
        {
            throw new InvalidOperationException(
                "Redis Cluster mode is not supported because account, matching claim, and handoff " +
                "transactions use multi-key Lua scripts. Use standalone Redis or a managed " +
                "cluster-mode-disabled deployment.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class RedLockFactoryAdapter(RedLockFactory redLockFactory) : IRedLockFactory
{
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LockWaitTime = TimeSpan.FromSeconds(3);
    private int _disposed;

    public async Task<IRedLock> AcquireLockAsync(string resource, TimeSpan expiryTime)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IRedLock redLock = await redLockFactory.CreateLockAsync(
            resource,
            expiryTime,
            LockWaitTime,
            LockRetryInterval);
        if (redLock.IsAcquired) return redLock;

        await redLock.DisposeAsync();
        throw new RedisLockNotAcquiredException(resource);
    }

    public async Task ExecuteWithLockAsync(string resource, TimeSpan expiryTime, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using IRedLock redLock = await AcquireLockAsync(resource, expiryTime);
        await action();
    }

    public async Task<T> ExecuteWithLockAsync<T>(
        string resource,
        TimeSpan expiryTime,
        Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using IRedLock redLock = await AcquireLockAsync(resource, expiryTime);
        return await action();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            redLockFactory.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
