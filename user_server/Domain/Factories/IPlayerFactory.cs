using network.common.data.models;
using user_server.domain.player;
using user_server.infrastructure.network;

namespace user_server.domain.factories;

/// <summary>
/// Factory interface for creating Player aggregates
/// </summary>
public interface IPlayerFactory
{
    /// <summary>
    /// Creates a new Player instance
    /// </summary>
    /// <param name="session">The game session</param>
    /// <param name="playerInfo">Player information</param>
    /// <returns>A new Player instance</returns>
    Player CreatePlayer(GameSession session, PlayerInfo playerInfo);
}
