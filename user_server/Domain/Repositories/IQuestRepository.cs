using network.common.data.models;

namespace user_server.domain.repositories;

/// <summary>
/// Repository interface for Quest data access
/// </summary>
public interface IQuestRepository
{
    /// <summary>
    /// Loads quest diary for a player
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <returns>Quest diary or null if not found</returns>
    Task<QuestDiary?> LoadAsync(long playerId);

    /// <summary>
    /// Saves quest diary to cache
    /// </summary>
    /// <param name="questDiary">Quest diary to save</param>
    Task SaveAsync(QuestDiary questDiary);

    /// <summary>
    /// Gets a specific quest by ID
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <param name="questId">Quest ID</param>
    /// <returns>Quest info or null if not found</returns>
    Task<QuestInfo?> GetQuestAsync(long playerId, int questId);
}
