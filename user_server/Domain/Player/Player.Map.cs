using network.common;
using network.common.data.models;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate - Map 관련 기능
/// </summary>
public partial class Player
{
    public async Task ChangeMap(MapId mapId)
    {
        await _mapManager.ChangeMap(mapId);
    }

    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        await _mapManager.EnterMap(mapId, spawnPosition, isFlip, isLogin);
    }

    public async Task EnterCamp(long mapSubId)
    {
        await _mapManager.EnterCamp(mapSubId);
    }
}
