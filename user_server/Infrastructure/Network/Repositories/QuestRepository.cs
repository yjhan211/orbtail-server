using network.common.data.models;
using network.interfaces;
using user_server.domain.repositories;

namespace user_server.infrastructure.repositories;

/// <summary>
/// Redis-based implementation of Quest repository
/// </summary>
public class QuestRepository : IQuestRepository
{
    private readonly ICacheHelper _cacheHelper;

    public QuestRepository(ICacheHelper cacheHelper)
    {
        _cacheHelper = cacheHelper;
    }

    public async Task<QuestDiary?> LoadAsync(long playerId)
    {
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);
        return playerInfo?.QuestDiary;
    }

    public async Task SaveAsync(QuestDiary questDiary)
    {
        await questDiary.Save(_cacheHelper);
    }

    public async Task<QuestInfo?> GetQuestAsync(long playerId, int questId)
    {
        var questDiary = await LoadAsync(playerId);
        if (questDiary?.QuestDict.TryGetValue(questId, out var questInfo) == true)
        {
            return questInfo;
        }
        return null;
    }
}
