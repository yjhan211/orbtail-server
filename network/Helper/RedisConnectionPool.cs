using StackExchange.Redis;
using StackExchange.Redis.MultiplexerPool;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;

namespace network
{
    public static class RedisConnectionPool
    {
        private static Lazy<ConnectionMultiplexer>? _lazyConnection;
        private static object _lock = new object();

        public static void Initialize(string connectionString)
        {
            _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
            {
                return ConnectionMultiplexer.Connect(connectionString);
            });
        }

        public static ConnectionMultiplexer GetConnection()
        {
            return _lazyConnection!.Value;
        }

        public static IDatabase GetDatabase(int db = -1)
        {
            return GetConnection().GetDatabase(db);
        }

        public static async Task<IDatabase> GetDatabaseAsync(int db = -1)
        {
            return await Task.FromResult(GetDatabase(db));
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                if (_lazyConnection!.IsValueCreated)
                {
                    _lazyConnection.Value.Dispose();
                }
            }
        }

        public static RedLockFactory GetRedLockFactory(ConnectionMultiplexer connection)
        {
            var endpoints = connection
                .GetEndPoints()
                .Select(endpoint => new RedLockEndPoint { EndPoint = endpoint })
                .ToList();

            var redLockFactory = RedLockFactory.Create(endpoints);
            return redLockFactory;
        }
    }
}
