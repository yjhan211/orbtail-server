using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

namespace network.interfaces;

public interface IRedisConnectionPool
{
    void Initialize(string connectionString);
    IRedLockFactory GetRedLockFactory();
    Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, int db = -1, int retryCount = 3);
    void Dispose();
}

public interface IRedLockFactory
{
    Task<IRedLock> CreateLockAsync(string resource, TimeSpan expiryTime);
}
