using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Quest 관련 기능
/// </summary>
public partial class Player
{
    public async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body)
    {
        await _questManager.IncreaseQuestCount(body);
    }

    public async Task CompleteQuest(C_TO_U_QUEST_SUCCESS body)
    {
        await _questManager.CompleteQuest(body);
    }

    public async Task StartQuest(int questId, List<QuestInfo>? updateQuests = null)
    {
        await _questManager.StartQuest(questId, updateQuests);
    }

    public void SendCurrentQuests()
    {
        _questManager.SendCurrentQuests();
    }
}
