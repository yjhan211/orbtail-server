using RedLockNet;
using RedLockNet.SERedis;

namespace network.infrastructure.redis;

/// <summary>
///     RedLock.net의 잠금 획득 결과를 서버 공용 계약으로 변환한다.
///     정해진 시간 동안 잠금을 재시도하고, 획득하지 못하면 예외를 발생시킨다.
/// </summary>
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
