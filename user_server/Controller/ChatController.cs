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
        static CacheHelper? cache_helper;
        const int HISTORY_NUM = 30;
        public static LRUCache<long, string> user_name_map = new(1000);

        public static void Initialize()
        {
            var connection = RedisConnectionPool.GetConnection();
            cache_helper = new(connection);
        }

        static string GetChatHistoryKey(ChatType chat_type)
        {
            return $"chat_{chat_type}_history";
        }

        public static void AddChatHistory(ChatType chat_type, string sender_name, string message)
        {
            var key = GetChatHistoryKey(ChatType.ALL);
            var history_length = cache_helper!.ListLength(key);

            while (history_length >= HISTORY_NUM)
            {
                cache_helper.Dequeue(key);
                history_length--;
            }

            cache_helper.Enqueue(
                key,
                MessagePackSerializer.Serialize((chat_type, sender_name, message))
            );
        }

        public static List<Packet> GetChatHistory(GameUser user, ChatType chat_type)
        {
            var key = GetChatHistoryKey(chat_type);
            var redis_values = user.cache_helper.ListRange(key);
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

        public static void SendChat(GameUser user, C_TO_U_CHAT_MSG body)
        {
            if (body.chat_message.Length >= Config.MAX_CHAT_LENGTH)
            {
                return;
            }

            var player_name = "";
            if (!user_name_map.TryGet(user.player_id, out player_name))
            {
                var player_info = PlayerInfoController.Load(cache_helper!, user.player_id);
                if (player_info == null)
                {
                    return;
                }

                user_name_map.Add(user.player_id, player_info.name);
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
                    AddChatHistory(body.chat_type, player_name, body.chat_message);
                    user.nats_client.Publish("all", packet.ToBytes());
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
