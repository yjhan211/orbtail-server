using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;
using RedLockNet.SERedis;

namespace user_server.players;

public class PlayerQuest(GameUser user, PlayerInfo playerInfo)
{
    private readonly CacheHelper _cacheHelper = user.CacheHelper;
    private readonly RedLockFactory _redLock = user.RedLock;
    private readonly SendPacketDelegate _sendToClient = user.Send;

    public async Task StartQuest(int questId, List<QuestInfo>? updateQuests = null)
    {
        var questDiary = await QuestDiary.Load(_cacheHelper, playerInfo.PlayerId);
        if (questDiary.QuestDict.ContainsKey(questId))
        {
            throw new Exception($"Already Started Quest. QuestId: {questId}");
        }

        var quest = new QuestInfo(playerInfo.PlayerId, questId);
        questDiary.AddQuest(quest);
        await questDiary.Save(_cacheHelper);

        updateQuests?.Add(quest);
    }

    public async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body)
    {
        var questDiary = await QuestDiary.Load(_cacheHelper, playerInfo.PlayerId);
        if (!questDiary.QuestDict.TryGetValue(body.QuestId, out var quest))
        {
            throw new Exception($"Not Progressed Quest. QuestId: {body.QuestId}");
        }

        if (quest.State == QuestState.END)
        {
            throw new Exception($"Already End Quest. QuestId: {body.QuestId}");
        }

        quest.Count += body.Count;
        await questDiary.Save(_cacheHelper);
        
        using var packet = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
        _sendToClient(packet);
    }
    
    public async Task IncreaseQuestCount(int questId, int count, List<QuestInfo> updateQuests)
    {
        var questDiary = await QuestDiary.Load(_cacheHelper, playerInfo.PlayerId);
        if (questDiary.QuestDict.TryGetValue(questId, out var quest))
        {
            quest.Count += count;
            await questDiary.Save(_cacheHelper);
            updateQuests.Add(quest);
        }
    }

    public async Task CompleteQuest(C_TO_U_QUEST_SUCCESS body)
    {
        var questDiary = await QuestDiary.Load(_cacheHelper, playerInfo.PlayerId);
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

        questInfo.State = QuestState.END;
        updateQuestList.Add(questInfo);

        if (questDesignData.RewardItemList.Count > 0)
        {
            var rewardMail = await PlayerMailBox.CreateMail(_cacheHelper, 2, questDesignData.RewardItemList);
            await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
            {
                var mailBox = await MailBox.Load(_cacheHelper, playerInfo.PlayerId);
                mailBox.AddMail(rewardMail);
                await mailBox.Save(_cacheHelper);
            }
        }
        
        var isAddNextQuest = true;
        foreach (var requireQuestId in questDesignData.NextRequire)
        {
            if (!questDiary.QuestDict.TryGetValue(requireQuestId, out var requireQuestInfo))
            {
                isAddNextQuest = false;
                break;
            }
            isAddNextQuest = requireQuestInfo.State == QuestState.END;
        }

        if (isAddNextQuest)
        {
            foreach (var nextQuestId in questDesignData.NextIdList)
            {
                var nextQuestInfo = new QuestInfo(playerInfo.PlayerId, nextQuestId);
                questDiary.AddQuest(nextQuestInfo);
                updateQuestList.Add(nextQuestInfo);
            }
        }
        
        await questDiary.Save(_cacheHelper);
        
        using var packet = PacketMaker.U_TO_C_QUEST_SUCCESS(body.QuestId, ErrorCode.SUCCESS);
        _sendToClient(packet);

        foreach (var updateQuestInfo in updateQuestList)
        {
            using var updateQuestPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuestInfo);
            _sendToClient(updateQuestPacket);
        }

        switch (body.QuestId)
        {
            case 100000015:
                playerInfo.IsTutorial = false;
                playerInfo.LastMapId = MapId.Gym;
                playerInfo.LastMapSubId = 0;
                await playerInfo.Save(_cacheHelper);
                break;
        }
    }
    
    public async Task SendCurrentQuests()
    {
        var questDiary = await QuestDiary.Load(_cacheHelper, playerInfo.PlayerId);
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
            _sendToClient(packet);
        }
    }
}