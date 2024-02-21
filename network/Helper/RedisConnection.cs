namespace network
{
    using RedLockNet.SERedis;
    using RedLockNet.SERedis.Configuration;
    using StackExchange.Redis;
    using System.Net.Sockets;

    public class RedisConnectionPool : IDisposable
    {
        public static ConfigurationOptions configOptions = new ConfigurationOptions
        {
            // Kubernetes 서비스 주소를 EndPoints에 추가
            EndPoints =
            {
                { "redis-cluster-leader.redis-operators.svc.cluster.local", 6379 },
                { "redis-cluster-follower.redis-operators.svc.cluster.local", 6379 }
            },
            CommandMap = CommandMap.Create(
                new HashSet<string> { "INFO", "CONFIG" },
                available: false
            ),
            KeepAlive = 180,
            // 필요한 경우 비밀번호 설정
            // Password = "yourpassword",
            Ssl = false, // Kubernetes 내부 통신에서는 일반적으로 SSL을 사용하지 않음
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AbortOnConnectFail = false // 클러스터 노드 중 하나에 연결 실패해도 연결 시도를 중단하지 않음
        };

        private static readonly Lazy<ConnectionMultiplexer> _multiplexer =
            new Lazy<ConnectionMultiplexer>(() =>
            {
                return ConnectionMultiplexer.Connect(configOptions);
            });

        public static ConnectionMultiplexer GetConnection()
        {
            return _multiplexer.Value;
        }

        public void Dispose()
        {
            if (_multiplexer.IsValueCreated)
            {
                _multiplexer.Value.Dispose();
            }
        }
    }

    public class RedisConnection : IDisposable
    {
        private long _lastReconnectTicks = DateTimeOffset.MinValue.UtcTicks;
        private DateTimeOffset _firstErrorTime = DateTimeOffset.MinValue;
        private DateTimeOffset _previousErrorTime = DateTimeOffset.MinValue;

        // StackExchange.Redis will also be trying to reconnect internally,
        // so limit how often we recreate the ConnectionMultiplexer instance
        // in an attempt to reconnect
        private readonly TimeSpan ReconnectMinInterval = TimeSpan.FromSeconds(60);

        // If errors occur for longer than this threshold, StackExchange.Redis
        // may be failing to reconnect internally, so we'll recreate the
        // ConnectionMultiplexer instance
        private readonly TimeSpan ReconnectErrorThreshold = TimeSpan.FromSeconds(30);
        private readonly TimeSpan RestartConnectionTimeout = TimeSpan.FromSeconds(15);
        private const int RetryMaxAttempts = 5;

        private SemaphoreSlim _reconnectSemaphore = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        public ConnectionMultiplexer _connection;
        public IDatabase? _database;
        private ISubscriber? _subscriber;

        public RedisConnection(ConnectionMultiplexer connection)
        {
            _connection = connection;
        }

        public RedLockFactory GetRedLockFactory()
        {
            return RedLockFactory.Create(
                new[] { new RedLockEndPoint(this._connection.GetEndPoints()[0]) }
            );
        }

        public static async Task<RedisConnection> InitializeAsync()
        {
            var connection = RedisConnectionPool.GetConnection();
            var redisConnection = new RedisConnection(connection);

            await redisConnection.ForceReconnectAsync(initializing: true);
            redisConnection._subscriber = redisConnection._connection.GetSubscriber();

            return redisConnection;
        }

        // In real applications, consider using a framework such as
        // Polly to make it easier to customize the retry approach.
        // For more info, please see: https://github.com/App-vNext/Polly
        public async Task<T> BasicRetryAsync<T>(Func<IDatabase, Task<T>> func)
        {
            int reconnectRetry = 0;

            while (true)
            {
                try
                {
                    if (_database == null)
                    {
                        throw new Exception();
                    }

                    return await func(_database);
                }
                catch (Exception ex)
                    when (ex is RedisConnectionException
                        || ex is SocketException
                        || ex is ObjectDisposedException
                    )
                {
                    reconnectRetry++;
                    if (reconnectRetry > RetryMaxAttempts)
                    {
                        throw;
                    }

                    try
                    {
                        await ForceReconnectAsync();
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        /// <summary>
        /// Force a new ConnectionMultiplexer to be created.
        /// NOTES:
        ///     1. Users of the ConnectionMultiplexer MUST handle ObjectDisposedExceptions, which can now happen as a result of calling ForceReconnectAsync().
        ///     2. Call ForceReconnectAsync() for RedisConnectionExceptions and RedisSocketExceptions. You can also call it for RedisTimeoutExceptions,
        ///         but only if you're using generous ReconnectMinInterval and ReconnectErrorThreshold. Otherwise, establishing new connections can cause
        ///         a cascade failure on a server that's timing out because it's already overloaded.
        ///     3. The code will:
        ///         a. wait to reconnect for at least the "ReconnectErrorThreshold" time of repeated errors before actually reconnecting
        ///         b. not reconnect more frequently than configured in "ReconnectMinInterval"
        /// </summary>
        /// <param name="initializing">Should only be true when ForceReconnect is running at startup.</param>
        private async Task ForceReconnectAsync(bool initializing = false)
        {
            long previousTicks = Interlocked.Read(ref _lastReconnectTicks);
            var previousReconnectTime = new DateTimeOffset(previousTicks, TimeSpan.Zero);
            TimeSpan elapsedSinceLastReconnect = DateTimeOffset.UtcNow - previousReconnectTime;

            // We want to limit how often we perform this top-level reconnect, so we check how long it's been since our last attempt.
            if (elapsedSinceLastReconnect < ReconnectMinInterval)
            {
                return;
            }

            bool lockTaken = await _reconnectSemaphore.WaitAsync(RestartConnectionTimeout);
            if (!lockTaken)
            {
                // If we fail to enter the semaphore, then it is possible that another thread has already done so.
                // ForceReconnectAsync() can be retried while connectivity problems persist.
                return;
            }

            try
            {
                var utcNow = DateTimeOffset.UtcNow;
                previousTicks = Interlocked.Read(ref _lastReconnectTicks);
                previousReconnectTime = new DateTimeOffset(previousTicks, TimeSpan.Zero);
                elapsedSinceLastReconnect = utcNow - previousReconnectTime;

                if (_firstErrorTime == DateTimeOffset.MinValue && !initializing)
                {
                    // We haven't seen an error since last reconnect, so set initial values.
                    _firstErrorTime = utcNow;
                    _previousErrorTime = utcNow;
                    return;
                }

                if (elapsedSinceLastReconnect < ReconnectMinInterval)
                {
                    return; // Some other thread made it through the check and the lock, so nothing to do.
                }

                TimeSpan elapsedSinceFirstError = utcNow - _firstErrorTime;
                TimeSpan elapsedSinceMostRecentError = utcNow - _previousErrorTime;

                bool shouldReconnect =
                    elapsedSinceFirstError >= ReconnectErrorThreshold // Make sure we gave the multiplexer enough time to reconnect on its own if it could.
                    && elapsedSinceMostRecentError <= ReconnectErrorThreshold; // Make sure we aren't working on stale data (e.g. if there was a gap in errors, don't reconnect yet).

                // Update the previousErrorTime timestamp to be now (e.g. this reconnect request).
                _previousErrorTime = utcNow;

                if (!shouldReconnect && !initializing)
                {
                    return;
                }

                _firstErrorTime = DateTimeOffset.MinValue;
                _previousErrorTime = DateTimeOffset.MinValue;

                if (_connection != null)
                {
                    try
                    {
                        await _connection.CloseAsync();
                    }
                    catch
                    {
                        // Ignore any errors from the old connection
                    }
                }

#pragma warning disable CS8601
                Interlocked.Exchange(ref _connection, null);
#pragma warning restore
                ConnectionMultiplexer newConnection = await ConnectionMultiplexer.ConnectAsync(
                    RedisConnectionPool.configOptions
                );
                Interlocked.Exchange(ref _connection, newConnection);

                Interlocked.Exchange(ref _lastReconnectTicks, utcNow.UtcTicks);
                IDatabase newDatabase = _connection.GetDatabase();
                Interlocked.Exchange(ref _database, newDatabase);
            }
            finally
            {
                _reconnectSemaphore.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                _connection?.Dispose();
            }
            catch { }
        }
    }
}
