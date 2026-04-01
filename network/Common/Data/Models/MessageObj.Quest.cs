#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class U_TO_C_QUEST_LIST : IMessagePackObject
    {
        [Key("questDict")] public Dictionary<int, QuestInfo> QuestDict { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_QUEST_INCREASE : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
        [Key("count")] public int Count { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_QUEST_UPDATE : IMessagePackObject
    {
        [Key("quest")] public QuestInfo QuestInfo { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_QUEST_SUCCESS : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_QUEST_SUCCESS : IMessagePackObject
    {
        [Key("questId")] public int QuestId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }
}
