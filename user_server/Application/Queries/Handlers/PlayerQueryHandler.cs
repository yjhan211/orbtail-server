using user_server.application.queries.player;
using user_server.infrastructure.network;

namespace user_server.application.queries.handlers;

/// <summary>
/// Handles all player-related queries
/// </summary>
public class PlayerQueryHandler :
    IQueryHandler<GetPlayerInfoQuery, PlayerInfoResult>,
    IQueryHandler<GetPlayerQuestsQuery, PlayerQuestsResult>,
    IQueryHandler<GetPlayerItemsQuery, PlayerItemsResult>,
    IQueryHandler<GetPlayerMailsQuery, PlayerMailsResult>
{
    private readonly Func<long, GameSession?> _getSession;

    public PlayerQueryHandler(Func<long, GameSession?> getSession)
    {
        _getSession = getSession;
    }

    public Task<PlayerInfoResult> HandleAsync(GetPlayerInfoQuery query)
    {
        var session = _getSession(query.PlayerId);
        var playerInfo = session?.Player?.PlayerInfo;

        return Task.FromResult(new PlayerInfoResult(playerInfo));
    }

    public Task<PlayerQuestsResult> HandleAsync(GetPlayerQuestsQuery query)
    {
        var session = _getSession(query.PlayerId);
        var quests = session?.Player?.PlayerInfo.QuestDiary.QuestDict.Values.ToList();

        return Task.FromResult(new PlayerQuestsResult(quests));
    }

    public Task<PlayerItemsResult> HandleAsync(GetPlayerItemsQuery query)
    {
        var session = _getSession(query.PlayerId);
        var items = session?.Player?.PlayerInfo.InventoryInfo.ItemDict.Values.ToList();

        return Task.FromResult(new PlayerItemsResult(items));
    }

    public Task<PlayerMailsResult> HandleAsync(GetPlayerMailsQuery query)
    {
        var session = _getSession(query.PlayerId);
        var mails = session?.Player?.PlayerInfo.MailBox.MailDict.Values.ToList();

        return Task.FromResult(new PlayerMailsResult(mails));
    }
}
