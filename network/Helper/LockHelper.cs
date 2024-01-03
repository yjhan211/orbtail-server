using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;

namespace network
{
    public class LockHelper
    {
#pragma warning disable CS8618
        public RedLockFactory _redlock_factory;
#pragma warning restore

        public LockHelper(RedisConnection conn)
        {
            var redisEndpoints = new[] { new RedLockEndPoint(conn._connection.GetEndPoints()[0]) };
            this._redlock_factory = RedLockFactory.Create(redisEndpoints);
        }

        public async Task<IDisposable> AcquireLock(
            string lockKey,
            int retryCount = 3,
            TimeSpan expiryTime = default,
            TimeSpan retryDelay = default
        )
        {
            try
            {
                var disposer = new LockDisposer(
                    this._redlock_factory,
                    lockKey,
                    retryCount,
                    expiryTime,
                    retryDelay
                );

                await disposer.AcquireLockAsync(); // 동기적으로 락을 획득합니다.
                return disposer;
            }
            catch (Exception e)
            {
                throw new Exception($"AcquireLock Fail. {e.Message}");
            }
        }
    }

    class LockDisposer : IDisposable
    {
        private string lock_key;
        private int retry_count;
        private TimeSpan expiry_time;
        private TimeSpan retry_delay;
        private RedLockFactory _redlock_factory;

        public LockDisposer(
            RedLockFactory redLockFactory,
            string lockKey,
            int retryCount,
            TimeSpan expiryTime,
            TimeSpan retryDelay
        )
        {
            this.lock_key = lockKey;
            this.retry_count = retryCount;
            this.expiry_time = expiryTime == default ? TimeSpan.FromSeconds(30) : expiryTime;
            this.retry_delay = retryDelay;
            this._redlock_factory = redLockFactory;

            // 락을 획득하고 결과를 저장합니다.
            AcquireLockAsync().GetAwaiter().GetResult(); // 비동기 호출을 동기적으로 대기하며 실행합니다.
        }

        public async Task AcquireLockAsync()
        {
            if (_redlock_factory == null)
            {
                throw new Exception("RedLockFactory is not initialized.");
            }

            int attempt = 1;

            do
            {
                using (var redLock = await _redlock_factory.CreateLockAsync(lock_key, expiry_time))
                {
                    if (redLock.IsAcquired)
                    {
                        return;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Failed to acquire lock for key: {lock_key}. Retrying ({attempt}/{retry_count})..."
                        );
                        attempt++;

                        if (retry_delay != default)
                        {
                            await Task.Delay(retry_delay);
                        }
                    }
                }
            } while (attempt <= retry_count);

            Console.WriteLine(
                $"Failed to acquire lock for key: {lock_key} after multiple attempts."
            );

            throw new Exception($"Failed to acquire lock for key: {lock_key}");
        }

        public void Dispose()
        {
            ReleaseLock();
        }

        private void ReleaseLock()
        {
            if (_redlock_factory == null)
            {
                throw new Exception("RedLockFactory is not initialized.");
            }

            try
            {
                using (var redLock = _redlock_factory.CreateLock(lock_key, TimeSpan.Zero))
                {
                    if (redLock.IsAcquired)
                    {
                        redLock.Dispose();
                        // Console.WriteLine($"Lock released for key: {lock_key}");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"Failed to release lock for key: {lock_key}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error releasing lock for key {lock_key}: {ex.Message}");
                // 예외 처리를 원하는 방식으로 수행
            }
        }
    }
}
