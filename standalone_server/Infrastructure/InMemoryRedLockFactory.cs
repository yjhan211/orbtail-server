using System.Collections.Concurrent;
using RedLockNet;
using network.interfaces;

namespace standalone_server.infrastructure;

public sealed class InMemoryRedLockFactory : IRedLockFactory
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IRedLock> CreateLockAsync(string resource, TimeSpan expiryTime)
    {
        SemaphoreSlim semaphore = _locks.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new InMemoryRedLock(resource, semaphore, expiryTime);
    }

    private sealed class InMemoryRedLock : IRedLock
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly Timer? _expiryTimer;
        private int _released;

        public InMemoryRedLock(string resource, SemaphoreSlim semaphore, TimeSpan expiryTime)
        {
            Resource = resource;
            _semaphore = semaphore;
            LockId = Guid.NewGuid().ToString("N");
            IsAcquired = true;
            Status = RedLockStatus.Acquired;
            InstanceSummary = new RedLockInstanceSummary(1, 0, 0);

            if (expiryTime > TimeSpan.Zero && expiryTime != Timeout.InfiniteTimeSpan)
                _expiryTimer = new Timer(_ => Release(), null, expiryTime, Timeout.InfiniteTimeSpan);
        }

        public string Resource { get; }
        public string LockId { get; }
        public bool IsAcquired { get; private set; }
        public RedLockStatus Status { get; private set; }
        public RedLockInstanceSummary InstanceSummary { get; }
        public int ExtendCount => 0;

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _expiryTimer?.Dispose();
            IsAcquired = false;
            Status = RedLockStatus.Unlocked;
            _semaphore.Release();
        }
    }
}
