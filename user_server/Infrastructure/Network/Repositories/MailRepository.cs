using network.common.data.models;
using network.interfaces;
using user_server.domain.repositories;

namespace user_server.infrastructure.repositories;

/// <summary>
/// Redis-based implementation of Mail repository
/// </summary>
public class MailRepository : IMailRepository
{
    private readonly ICacheHelper _cacheHelper;

    public MailRepository(ICacheHelper cacheHelper)
    {
        _cacheHelper = cacheHelper;
    }

    public async Task<MailBox?> LoadAsync(long playerId)
    {
        var playerInfo = await PlayerInfo.Load(_cacheHelper, playerId);
        return playerInfo?.MailBox;
    }

    public async Task SaveAsync(MailBox mailBox)
    {
        await mailBox.Save(_cacheHelper);
    }

    public async Task<MailInfo?> GetMailAsync(long playerId, long mailUid)
    {
        var mailBox = await LoadAsync(playerId);
        if (mailBox?.MailDict.TryGetValue(mailUid, out var mailInfo) == true)
        {
            return mailInfo;
        }
        return null;
    }
}
