using MessagePack;
using network.interfaces;

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

    public async Task Save(ICacheHelper cacheHelper)
    {
        if (QuestDict.Count == 0)
        {
            await cacheHelper.HashDeleteAsync(HashKey, PlayerId);
            return;
        }
        await cacheHelper.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<QuestDiary> Load(ICacheHelper cacheHelper, long playerId)
    {
        var serializedData = await cacheHelper.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return new QuestDiary(playerId);

        var quests = MessagePackSerializer.Deserialize<QuestDiary>(serializedData);
        return quests;
    }

    public static async Task Delete(ICacheHelper cacheHelper, long playerId)
    {
        await cacheHelper.HashDeleteAsync(HashKey, playerId);
    }
}
