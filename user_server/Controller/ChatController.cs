namespace user_server
{
    using game_server;
    using MessagePack;
    using network;
    using network.manager;
    using StackExchange.Redis;

    // TODO 도배금지, 채팅금지 등
    public static class ChatController
    {
        const int HISTORY_NUM = 30;
        public static LRUCache<long, string> user_name_map = new(1000);

        static string GetChatHistoryKey(ChatType chat_type)
        {
            return $"chat_{chat_type}_history";
        }

        public static async Task AddChatHistory(
            CacheHelper cache_helper,
            ChatType chat_type,
            string sender_name,
            string message
        )
        {
            var key = GetChatHistoryKey(ChatType.ALL);
            var history_length = await cache_helper.ListLength(key);

            while (history_length >= HISTORY_NUM)
            {
                await cache_helper.Dequeue(key);
            }

            await cache_helper.Enqueue(
                key,
                MessagePackSerializer.Serialize((chat_type, sender_name, message))
            );
        }

        public static async Task<List<Packet>> GetChatHistory(
            CacheHelper cache_helper,
            ChatType chat_type
        )
        {
            var key = GetChatHistoryKey(chat_type);
            var redis_values = await cache_helper.ListRange(key);

            var result = new List<Packet>();

            foreach (var redis_value in redis_values)
            {
                if (redis_value == RedisValue.Null)
                {
                    continue;
                }

                (ChatType, string, string) deserialize;

                try
                {
                    deserialize = MessagePackSerializer.Deserialize<(ChatType, string, string)>(
                        redis_value
                    );
                }
                catch (MessagePackSerializationException)
                {
                    continue;
                }

                var packet = PacketMaker.U_TO_C_CHAT_MSG(
                    deserialize.Item1,
                    deserialize.Item2,
                    deserialize.Item3
                );

                result.Add(packet);
            }

            return result;
        }

        public static async Task SendChat(
            CacheHelper cache_helper,
            NatsClient nats_client,
            RedisValue message
        )
        {
            var (player_id, body) = MessagePackSerializer.Deserialize<(long, C_TO_U_CHAT_MSG)>(
                message
            );

            var player_name = "";
            if (!user_name_map.TryGet(player_id, out player_name))
            {
                var player_info = await PlayerController.Load(cache_helper, player_id);
                if (player_info == null)
                {
                    return;
                }

                user_name_map.Add(player_id, player_info.name);
                player_name = player_info.name;
            }

            Packet packet = PacketMaker.U_TO_C_CHAT_MSG(
                body.chat_type,
                player_name,
                body.chat_message
            );

            switch (body.chat_type)
            {
                case ChatType.ALL:
                    await AddChatHistory(
                        cache_helper,
                        body.chat_type,
                        player_name,
                        body.chat_message
                    );
                    nats_client.Publish("all", packet.ToBytes());
                    break;

                case ChatType.NOMAL:
                    break;

                case ChatType.GUILD:
                    break;
            }

            Packet.Destroy(packet);
        }
    }
}
