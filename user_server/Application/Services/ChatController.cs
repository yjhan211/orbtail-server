using MessagePack;
using Microsoft.AspNetCore.Identity;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;
using StackExchange.Redis;

namespace user_server.application.services;

public class ChatController(ICacheHelper cacheHelper)
{
    private readonly LruCache<long, string> _userNameMap = new(1000);
    private const int HistoryNum = 30;

    private static string GetChatHistoryKey(ChatType chatType)
    {
        return $"chat_{chatType}_history";
    }

    private async Task AddChatHistory(ChatType chatType, long playerId, string senderName, string message)
    {
        var key = GetChatHistoryKey(ChatType.ALL);
        var historyLength = await cacheHelper.ListLengthAsync(key);

        while (historyLength >= HistoryNum)
        {
            await cacheHelper.DequeueAsync(key);
            historyLength--;
        }

        await cacheHelper.EnqueueAsync(key, MessagePackSerializer.Serialize((chatType, playerId, senderName, message)));
    }

    public async Task<List<(ChatType, long, string, string)>> GetChatHistory(ChatType chatType)
    {
        var key = GetChatHistoryKey(chatType);
        var redisValues = await cacheHelper.ListRangeAsync(key);

        List<(ChatType, long, string, string)> chatHistory = [];
        foreach (var value in redisValues)
        {
            if (value == RedisValue.Null)
            {
                continue;
            }

            (ChatType, long, string, string) deserialize;

            try
            {
                deserialize = MessagePackSerializer.Deserialize<(ChatType, long, string, string)>(value);
            }
            catch (MessagePackSerializationException)
            {
                continue;
            }

            chatHistory.Add(deserialize);
        }

        return chatHistory;
    }

    public async Task SendChat(long playerId, string name, ChatType chatType, string message, INatsClient natsClient)
    {
        if (!_userNameMap.TryGet(playerId, out var _))
        {
            _userNameMap.Add(playerId, name);
        }

        using var packet = PacketMaker.U_TO_C_CHAT_MSG(chatType, playerId, name, message);
        switch (chatType)
        {
            case ChatType.ALL:
                await AddChatHistory(chatType, playerId, name, message);
                natsClient.Publish("all", packet.ToBytes());
                break;

            case ChatType.NORMAL:
            case ChatType.GUILD:
                break;
        }
    }
}
