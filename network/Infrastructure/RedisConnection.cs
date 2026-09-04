using System.Collections.Concurrent;

using network.interfaces;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;

namespace network.infrastructure;

/// <summary>
///     서버 프로세스에서 Redis 연결을 생성하고 공유한다.
///     초기화할 때 하나의 ConnectionMultiplexer를 생성하고 계속 재사용한다.
///     DB별 IDatabase 객체와 필요한 경우 RedLock 팩토리를 함께 관리한다.
///     연결 생성과 종료를 담당한다.
/// </summary>
public sealed class RedisConnection : IRedisConnection
{
    private readonly ConcurrentDictionary<int, IDatabase> _databases = new();
    private readonly object _lock = new();
    private ConnectionMultiplexer? _connection;
    private RedLockFactoryAdapter? _redLockFactory;

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
            if (_connection != null)
            {
                throw new InvalidOperationException("Redis connection is already initialized.");
            }

            ConnectionMultiplexer? connection = null;
            try
            {
                connection = ConnectionMultiplexer.Connect(configuration.CreateClientOptions());
                _connection = connection;
            }
            catch (Exception ex)
            {
                connection?.Dispose();
                throw new InvalidOperationException("Failed to connect to Redis.", ex);
            }
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
            connection = _connection;
            _connection = null;
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

    private ConnectionMultiplexer GetConnection()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_connection == null) throw new InvalidOperationException("Redis connection is not initialized.");
            return _connection;
        }
    }

    public IDatabase GetDatabase(int db = -1)
    {
        return _databases.GetOrAdd(db, dbNum => GetConnection().GetDatabase(dbNum));
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

        var redLock = await redLockFactory.CreateLockAsync(
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

        await using var redLock = await AcquireLockAsync(resource, expiryTime);
        await action();
    }

    public async Task<T> ExecuteWithLockAsync<T>(
        string resource,
        TimeSpan expiryTime,
        Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var redLock = await AcquireLockAsync(resource, expiryTime);
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
