using MessagePack;
using network.helpers;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

public partial class QuestDiary
{
    public void AddQuest(QuestInfo questInfo)
    {
        QuestDict[questInfo.QuestId] = questInfo;
    }

    public void AddQuest(List<QuestInfo> questInfoList)
    {
        foreach (var questInfo in questInfoList)
        {
            QuestDict[questInfo.QuestId] = questInfo;
        }
    }

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<QuestDiary> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return new QuestDiary(playerId);
        
        var quests = MessagePackSerializer.Deserialize<QuestDiary>(serializedData);
        return quests;
    }

    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}
