using RedLockNet;
using StackExchange.Redis;

namespace network.infrastructure.redis;

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
