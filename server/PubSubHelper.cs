using StackExchange.Redis;

namespace game_server
{
    public static class PubSubHelper
    {
#pragma warning disable CS8618
        static RedisConnection conn;
#pragma warning restore
        public static void Initialize(RedisConnection conn)
        {
            PubSubHelper.conn = conn;
            Console.WriteLine("PubSubHelper Initialize success");
        }

        public static ISubscriber GetSubscriber()
        {
            return conn._connection.GetSubscriber();
        }
    }
}
