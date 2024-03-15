namespace social_server
{
    using System;
    using System.Threading.Tasks;
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public class SocialServer
    {
        ConnectionMultiplexer redis_connection;
        CacheHelper cache_helper;
        NatsClient nats_client;
        ChatController chat_controller;

        public SocialServer()
        {
            this.nats_client = new(Program.nats_endpoint);

            this.redis_connection = RedisConnectionPool.GetConnection();
            this.cache_helper = new(this.redis_connection);

            this.chat_controller = new(this.cache_helper, this.nats_client);
        }
    }
}
