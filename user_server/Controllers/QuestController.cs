using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace user_server.controllers;

public static class QuestController
{
    public static async Task StartQuest(GameUser user, int questId, List<QuestInfo> updateQuests)
    {
        var questDiary = await QuestDiary.Load(user.PlayerId);
        if (questDiary.QuestDict.ContainsKey(questId))
        {
            throw new Exception($"Already Started Quest. QuestId: {questId}");
        }

        var quest = new QuestInfo(user.PlayerId, questId);
        questDiary.AddQuest(quest);
        await questDiary.Save();
        
        updateQuests.Add(quest);
    }

    public static async Task IncreaseQuestCount(GameUser user, C_TO_U_QUEST_INCREASE body)
    {
        var questDiary = await QuestDiary.Load(user.PlayerId);
        if (!questDiary.QuestDict.TryGetValue(body.QuestId, out var quest))
        {
            throw new Exception($"Not Progressed Quest. QuestId: {body.QuestId}");
        }

        if (quest.State == QuestState.END)
        {
            throw new Exception($"Already End Quest. QuestId: {body.QuestId}");
        }

        quest.Count += body.Count;
        await questDiary.Save();
        
        using var packet = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
        user.Send(packet);
    }
    
    public static async Task IncreaseQuestCount(GameUser user, int questId, int count, List<QuestInfo> updateQuests)
    {
        var questDiary = await QuestDiary.Load(user.PlayerId);
        if (questDiary.QuestDict.TryGetValue(questId, out var quest))
        {
            quest.Count += count;
            await questDiary.Save();
            updateQuests.Add(quest);
        }
    }

    public static async Task CompleteQuest(GameUser user, C_TO_U_QUEST_INCREASE body)
    {
        var questDiary = await QuestDiary.Load(user.PlayerId);
        var updateQuestList = new List<QuestInfo>();
        if (!questDiary.QuestDict.TryGetValue(body.QuestId, out var questInfo))
        {
            throw new Exception($"Not Started Quest. QuestId: {body.QuestId}");
        }
            
        var questDesignData = GameQuestData.Get(body.QuestId);
        if (questInfo.Count < questDesignData.RequireCount)
        {
            throw new Exception($"Invalid State. QuestId: {body.QuestId}");
        }

        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null)
        {
            throw new Exception($"Invalid Player Id. PlayerId: {user.PlayerId}");
        }

        questInfo.State = QuestState.END;
        updateQuestList.Add(questInfo);

        if (questDesignData.RewardItemList.Count > 0)
        {
            var rewardMail = await MailBoxController.CreateMail(2, questDesignData.RewardItemList);
            await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
            {
                var mailBox = await MailBox.Load(user.PlayerId);
                mailBox.AddMail(rewardMail);
                await mailBox.Save();
            }
        }
        
        var isAddNextQuest = false;
        foreach (var requireQuestId in questDesignData.NextRequire)
        {
            if (questDiary.QuestDict.TryGetValue(requireQuestId, out var requireQuestInfo))
            {
                isAddNextQuest = requireQuestInfo.State == QuestState.END;
            }
        }

        if (isAddNextQuest)
        {
            foreach (var nextQuestId in questDesignData.NextIdList)
            {
                var nextQuestInfo = new QuestInfo(user.PlayerId, nextQuestId);
                questDiary.AddQuest(nextQuestInfo);
                updateQuestList.Add(nextQuestInfo);
            }
        }
        
        await questDiary.Save();
        
        using var packet = PacketMaker.U_TO_C_QUEST_SUCCESS(body.QuestId, ErrorCode.SUCCESS);
        user.Send(packet);

        foreach (var updateQuestInfo in updateQuestList)
        {
            using var updateQuestPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuestInfo);
            user.Send(updateQuestPacket);
        }
    }
    
    public static async Task GetCurrentQuestList(GameUser user)
    {
        var questDiary = await QuestDiary.Load(user.PlayerId);
        if (questDiary.QuestDict.Count == 0)
        {
            return;
        }

        var questKeys = questDiary.QuestDict.Keys.ToArray();
        for (var i = 0; i < questKeys.Length; i += Config.BROADCAST_UNIT)
        {
            var batchDict = questKeys.Skip(i).Take(Config.BROADCAST_UNIT)
                .ToDictionary(key => key, key => questDiary.QuestDict[key]);

            var isEnded = i + Config.BROADCAST_UNIT >= questKeys.Length;

            using var packet = PacketMaker.U_TO_C_QUEST_LIST(batchDict, isEnded);
            user.Send(packet);
        }
    }
}
