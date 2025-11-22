// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class QuestDiary : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "QuestDiary";
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("questDict")] public Dictionary<int, QuestInfo> QuestDict { get; set; }

        public QuestDiary()
        {
            PlayerId = 0;
            QuestDict = new();
        }

        public QuestDiary(long playerId)
        {
            PlayerId = playerId;
            QuestDict = new();
        }
    }

    [MessagePackObject]
    public partial class QuestInfo : IMessagePackObject
    {
        public QuestInfo()
        {
            QuestId = 0;
            Count = 0;
            State = QuestState.NONE;
        }

        public QuestInfo(long playerId, int questId)
        {
            QuestId = questId;
            Count = 0;
            State = QuestState.NONE;
        }

        [Key("questId")] public int QuestId { get; set; }
        [Key("count")] public int Count { get; set; }
        [Key("state")] public QuestState State { get; set; }
    }
}