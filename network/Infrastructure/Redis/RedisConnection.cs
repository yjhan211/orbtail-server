using System.Collections.Concurrent;

using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;

namespace network.infrastructure.redis;

/// <summary>
///     서버 프로세스에서 Redis 연결을 생성하고 공유한다.
///     생성할 때 하나의 ConnectionMultiplexer를 연결하고 계속 재사용한다.
///     DB별 IDatabase 객체와 필요한 경우 RedLock 팩토리를 함께 관리한다.
///     연결 생성과 종료를 담당한다.
/// </summary>
public sealed class RedisConnection : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, IDatabase> _databases = new();
    private readonly object _lock = new();
    private ConnectionMultiplexer? _connection;
    private RedLockFactoryAdapter? _redLockFactory;

    private bool _disposed;

    public RedisConnection(string connectionString)
        : this(RedisConfigurationParser.ParseConnectionString(connectionString))
    {
    }

    public RedisConnection(RedisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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

    public void Dispose()
    {
        RedLockFactoryAdapter? redLockFactory;
        ConnectionMultiplexer? connection;

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
