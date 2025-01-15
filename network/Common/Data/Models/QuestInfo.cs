// ReSharper disable All
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class QuestInfo
    {
        [IgnoreMember] public const string HashKey = "QuestInfo";

        public QuestInfo()
        {
            PlayerId = 0;
            QuestId = 0;
            Count = 0;
            State = QuestState.NONE;
        }

        public QuestInfo(long playerId, int questId)
        {
            PlayerId = playerId;
            QuestId = questId;
            Count = 0;
            State = QuestState.NONE;
        }
        
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("questId")] public int QuestId { get; set; }
        [Key("count")] public int Count { get; set; }
        [Key("state")] public QuestState State { get; set; }
    }
}