using RedLockNet;
using StackExchange.Redis;

namespace network.interfaces;

/// <summary>
///     Redis 연결 포트. 프로세스가 공유할 연결을 초기화하고 DB view와 RedLock 팩토리를 제공한다.
/// </summary>
public interface IRedisConnection : IDisposable, IAsyncDisposable
{
    public void Initialize(string connectionString);
    public void Initialize(RedisConfiguration configuration);
    public IDatabase GetDatabase(int db = -1);
    public IRedLockFactory GetRedLockFactory();
}

public interface IRedLockFactory : IDisposable, IAsyncDisposable
{
    public Task<IRedLock> AcquireLockAsync(string resource, TimeSpan expiryTime);
    public Task ExecuteWithLockAsync(string resource, TimeSpan expiryTime, Func<Task> action);
    public Task<T> ExecuteWithLockAsync<T>(string resource, TimeSpan expiryTime, Func<Task<T>> action);
}

public sealed class RedisLockNotAcquiredException(string resource)
    : InvalidOperationException($"Redis lock could not be acquired for resource '{resource}'.")
{
    public string Resource { get; } = resource;
}
