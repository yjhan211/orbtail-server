using network.common.data.models;
using user_server.domain.factories;
using user_server.domain.player;
using user_server.infrastructure.network;

namespace user_server.infrastructure.factories;

/// <summary>
/// Factory implementation for creating Player aggregates
/// </summary>
public class PlayerFactory : IPlayerFactory
{
    public Player CreatePlayer(GameSession session, PlayerInfo playerInfo)
    {
        return new Player(session, playerInfo);
    }
}
