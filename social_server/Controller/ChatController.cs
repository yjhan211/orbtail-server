namespace social_server
{
    using game_server;
    using MessagePack;
    using network;
    using network.manager;
    using StackExchange.Redis;
    using user_server;

    // TODO 도배금지, 채팅금지 등
    public class ChatController
    {
        CacheHelper cache_helper;
        NatsClient nats_client;
        LRUCache<long, string> user_name_map;
        FixedSizeQueue<Packet> chat_cache;

        public ChatController(CacheHelper cache_helper, NatsClient nats_client)
        {
            this.cache_helper = cache_helper;
            this.nats_client = nats_client;
            this.user_name_map = new(1000);
            this.chat_cache = new(30, Packet.Destroy);

            _ = this.nats_client.Subscribe($"chat", async (subject, msg) => await SendChat(msg));
            _ = this.nats_client.Subscribe($"chat_history", (subject, msg) => GetChatHistory(msg));
        }

        async Task SendChat(RedisValue message)
        {
            LogManager.WriteInfoLog("send chat");

            var (player_id, body) = MessagePackSerializer.Deserialize<(long, C_TO_U_CHAT_MSG)>(
                message
            );

            var player_name = "";
            if (!this.user_name_map.TryGet(player_id, out player_name))
            {
                var player_info = await PlayerController.Load(this.cache_helper, player_id);
                if (player_info == null)
                {
                    return;
                }

                this.user_name_map.Add(player_id, player_name);
                player_name = player_info.name;
            }

            Packet packet = PacketMaker.S_TO_U_CHAT_MSG(
                body.chat_type,
                player_name,
                body.chat_message
            );

            switch (body.chat_type)
            {
                case ChatType.ALL:
                    this.chat_cache.Enqueue(packet);
                    this.nats_client.Publish("all", packet.ToBytes());
                    break;

                case ChatType.NOMAL:
                    // 맵 단위로 나눠야되나...
                    break;

                case ChatType.GUILD:
                    break;
            }
        }

        void GetChatHistory(RedisValue message)
        {
            var user_topic = MessagePackSerializer.Deserialize<string>(message);
            foreach (var packet in this.chat_cache.ToList())
            {
                this.nats_client.Publish(user_topic, packet.ToBytes());
            }
        }
    }
}
