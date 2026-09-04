using RedLockNet;

namespace network.infrastructure.redis;

public interface IRedLockFactory : IDisposable, IAsyncDisposable
{
    public Task<IRedLock> AcquireLockAsync(string resource, TimeSpan expiryTime);
    public Task ExecuteWithLockAsync(string resource, TimeSpan expiryTime, Func<Task> action);
    public Task<T> ExecuteWithLockAsync<T>(string resource, TimeSpan expiryTime, Func<Task<T>> action);
}
