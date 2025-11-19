using network.common;
using network.common.data;
using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Movement 관련 기능
/// </summary>
public partial class Player
{
    public async Task RequestMove(C_TO_U_MOVE body)
    {
        await _movementManager.HandleMove(body);
    }

    public async Task Spawn()
    {
        await _movementManager.Spawn();
    }
}
