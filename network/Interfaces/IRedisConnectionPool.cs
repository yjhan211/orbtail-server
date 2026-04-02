using RedLockNet;
using StackExchange.Redis;

namespace network.interfaces;

public interface IRedisConnectionPool
{
    public void Initialize(string connectionString);
    public IRedLockFactory GetRedLockFactory();
    public Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3);
    public void Dispose();
}

public interface IRedLockFactory
{
    public Task<IRedLock> CreateLockAsync(string resource, TimeSpan expiryTime);
}
