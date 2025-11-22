using network.common.data.models;

namespace user_server.domain.repositories;

/// <summary>
/// Repository interface for Player data access
/// </summary>
public interface IPlayerRepository
{
    /// <summary>
    /// Loads player information from cache
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <returns>Player information or null if not found</returns>
    Task<PlayerInfo?> LoadAsync(long playerId);

    /// <summary>
    /// Saves player information to cache
    /// </summary>
    /// <param name="playerInfo">Player information to save</param>
    Task SaveAsync(PlayerInfo playerInfo);

    /// <summary>
    /// Deletes player information from cache
    /// </summary>
    /// <param name="playerId">Player ID</param>
    Task DeleteAsync(long playerId);

    /// <summary>
    /// Checks if player exists in cache
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <returns>True if exists, false otherwise</returns>
    Task<bool> ExistsAsync(long playerId);
}
