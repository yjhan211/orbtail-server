using RedLockNet;

namespace network.infrastructure.redis;

/// <summary>
///     운영 구현체는 하나지만, 분산 잠금을 로직을 Redis 서버 없이 테스트하기 위해 인터페이스로 분리.
/// </summary>
public interface IRedLockFactory : IDisposable, IAsyncDisposable
{
    public Task<IRedLock> AcquireLockAsync(string resource, TimeSpan expiryTime);
    public Task ExecuteWithLockAsync(string resource, TimeSpan expiryTime, Func<Task> action);
    public Task<T> ExecuteWithLockAsync<T>(string resource, TimeSpan expiryTime, Func<Task<T>> action);
}
