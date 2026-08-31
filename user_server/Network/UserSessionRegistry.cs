using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace user_server.network;

/// <summary>
///     Owns the process-local mapping from player ids to authenticated sessions and performs
///     compare-by-instance replacement/removal so a stale disconnect cannot evict a newer login.
/// </summary>
internal sealed class UserSessionRegistry(ILogger logger)
{
    private readonly ConcurrentDictionary<long, GameSession> _sessions = new();

    public Action? Register(long playerId, GameSession session)
    {
        while (true)
        {
            if (!_sessions.TryGetValue(playerId, out GameSession? existingSession))
            {
                if (_sessions.TryAdd(playerId, session))
                {
                    logger.LogInformation("Session registered: PlayerId={PlayerId}", playerId);
                    return null;
                }

                continue;
            }

            if (ReferenceEquals(existingSession, session))
                return null;

            if (!_sessions.TryUpdate(playerId, session, existingSession))
                continue;

            logger.LogWarning("Session replaced after duplicate login: PlayerId={PlayerId}", playerId);
            return existingSession.DisconnectForDuplicateLogin;
        }
    }

    public bool Remove(long playerId, GameSession session)
    {
        bool removed = ((ICollection<KeyValuePair<long, GameSession>>)_sessions)
            .Remove(new KeyValuePair<long, GameSession>(playerId, session));
        if (removed)
            logger.LogInformation("Session removed: PlayerId={PlayerId}", playerId);
        return removed;
    }

    public GameSession? Get(long playerId)
    {
        _sessions.TryGetValue(playerId, out GameSession? session);
        return session;
    }

    public GameSession[] Snapshot()
    {
        return _sessions.Values.ToArray();
    }
}
