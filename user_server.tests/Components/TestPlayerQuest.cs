using network.common.data.models;
using user_server.players;

namespace user_server.tests.components;

public class TestPlayerQuest : PlayerQuest
{
    public int IncreaseQuestCallCount { get; private set; }
    public List<int> IncreasedQuestIds { get; private set; }
    
    public TestPlayerQuest(GameUser user, PlayerInfo playerInfo) 
        : base(user, playerInfo)
    {
        IncreaseQuestCallCount = 0;
        IncreasedQuestIds = new List<int>();
    }
    
    public override async Task IncreaseQuestCount(C_TO_U_QUEST_INCREASE body)
    {
        IncreaseQuestCallCount++;
        IncreasedQuestIds.Add(body.QuestId);
        // 실제로 퀘스트 정보를 업데이트하지 않음
        return;
    }
}