using network.common.data.models;
using network.interfaces;
using user_server.domain.repositories;

namespace user_server.infrastructure.repositories;

/// <summary>
/// Redis-based implementation of Player repository
/// </summary>
public class PlayerRepository : IPlayerRepository
{
    private readonly ICacheHelper _cacheHelper;

    public PlayerRepository(ICacheHelper cacheHelper)
    {
        _cacheHelper = cacheHelper;
    }

    public async Task<PlayerInfo?> LoadAsync(long playerId)
    {
        return await PlayerInfo.Load(_cacheHelper, playerId);
    }

    public async Task SaveAsync(PlayerInfo playerInfo)
    {
        await playerInfo.Save(_cacheHelper);
    }

    public async Task DeleteAsync(long playerId)
    {
        await PlayerInfo.Delete(_cacheHelper, playerId);
    }

    public async Task<bool> ExistsAsync(long playerId)
    {
        var playerInfo = await LoadAsync(playerId);
        return playerInfo != null;
    }
}
