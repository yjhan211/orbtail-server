using network.common;
using network.common.data;
using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Explore 관련 기능
/// </summary>
public partial class Player
{
    public async Task Explore(C_TO_U_EXPLORE body)
    {
        await _exploreManager.Explore(body);
    }
}
