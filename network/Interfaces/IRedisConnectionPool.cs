using RedLockNet;
using StackExchange.Redis;

namespace network.interfaces;

public interface IRedisConnectionPool : IDisposable, IAsyncDisposable
{
    public void Initialize(string connectionString);
    public void Initialize(RedisConfiguration configuration);
    public IRedLockFactory GetRedLockFactory();
    public Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3);
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
