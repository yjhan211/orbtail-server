using MessagePack;
using StackExchange.Redis;
using network.common;
using network.utils;
using network.helpers;
using network.packets;

namespace user_server.controllers
{
    public static class ChatController
    {
        const int HISTORY_NUM = 30;
        private readonly static LRUCache<long, string> _userNameMap = new(1000);

        private static string GetChatHistoryKey(ChatType chatType)
        {
            return $"chat_{chatType}_history";
        }

        public static async Task AddChatHistory(ChatType chatType, string senderName, string message)
        {
            var key = GetChatHistoryKey(ChatType.ALL);
            var historyLength = await CacheHelper.Instance.ListLengthAsync(key);

            while (historyLength >= HISTORY_NUM)
            {
                await CacheHelper.Instance.DequeueAsync(key);
                historyLength--;
            }

            await CacheHelper.Instance.EnqueueAsync(key, MessagePackSerializer.Serialize((chatType, senderName, message)));
        }

        public static async Task GetChatHistory(GameUser user, ChatType chatType)
        {
            var key = GetChatHistoryKey(chatType);
            var redisValues = await CacheHelper.Instance.ListRangeAsync(key);

            foreach (var value in redisValues)
            {
                if (value == RedisValue.Null)
                {
                    continue;
                }

                (ChatType, string, string) deserialize;

                try
                {
                    deserialize = MessagePackSerializer.Deserialize<(ChatType, string, string)>(value);
                }
                catch (MessagePackSerializationException)
                {
                    continue;
                }

                using var packet = PacketMaker.U_TO_C_CHAT_MSG(deserialize.Item1, deserialize.Item2, deserialize.Item3);
                user.SendToClient(packet);
            }
        }

        public static async Task SendChat(GameUser user, C_TO_U_CHAT_MSG body)
        {
            if (body.ChatMessage.Length >= Config.MAX_CHAT_LENGTH)
            {
                return;
            }

            if (!_userNameMap.TryGet(user.PlayerId, out string? playerName))
            {
                var playerInfo = await PlayerInfo.Load(user.PlayerId);
                if (playerInfo == null)
                {
                    return;
                }

                _userNameMap.Add(user.PlayerId, playerInfo.Name);
                playerName = playerInfo.Name;
            }

            using var packet = PacketMaker.U_TO_C_CHAT_MSG(body.ChatType, playerName!, body.ChatMessage);
            switch (body.ChatType)
            {
                case ChatType.ALL:
                    await AddChatHistory(body.ChatType, playerName!, body.ChatMessage);
                    user.NatsClient.Publish("all", packet.ToBytes());
                    break;

                case ChatType.NOMAL:
                    break;

                case ChatType.GUILD:
                    break;
            }
        }
    }
}
