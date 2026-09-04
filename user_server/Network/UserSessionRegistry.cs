using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace user_server.network;

/// <summary>
///     프로세스 안의 현재 플레이어 세션을 보관한다. Redis에서 발급한 세대를 비교해
///     늦게 끝난 로그인이나 이전 세션의 종료가 더 최신 세션을 교체·제거하지 못하게 한다.
/// </summary>
internal sealed class UserSessionRegistry(ILogger logger)
{
    private readonly ConcurrentDictionary<long, GameSession> _sessions = new();

    public (bool Accepted, Action? DisconnectSuperseded) Register(long playerId, GameSession session)
    {
        if (session.SessionGeneration <= 0)
            throw new InvalidOperationException("A session must own a positive generation before registration.");

        while (true)
        {
            if (!_sessions.TryGetValue(playerId, out GameSession? existingSession))
            {
                if (_sessions.TryAdd(playerId, session))
                {
                    logger.LogInformation(
                        "Session registered: PlayerId={PlayerId}, Generation={Generation}",
                        playerId, session.SessionGeneration);
                    return (true, null);
                }

                continue;
            }

            if (ReferenceEquals(existingSession, session))
                return (true, null);

            if (existingSession.SessionGeneration >= session.SessionGeneration)
            {
                logger.LogWarning(
                    "Stale session registration rejected: PlayerId={PlayerId}, Generation={Generation}, CurrentGeneration={CurrentGeneration}",
                    playerId, session.SessionGeneration, existingSession.SessionGeneration);
                return (false, null);
            }

            if (!_sessions.TryUpdate(playerId, session, existingSession))
                continue;

            logger.LogWarning(
                "Session replaced after duplicate login: PlayerId={PlayerId}, Generation={Generation}, PreviousGeneration={PreviousGeneration}",
                playerId, session.SessionGeneration, existingSession.SessionGeneration);
            return (true, existingSession.DisconnectForDuplicateLogin);
        }
    }

    public bool Remove(long playerId, GameSession session)
    {
        bool removed = ((ICollection<KeyValuePair<long, GameSession>>)_sessions)
            .Remove(new KeyValuePair<long, GameSession>(playerId, session));
        if (removed)
            logger.LogInformation(
                "Session removed: PlayerId={PlayerId}, Generation={Generation}",
                playerId, session.SessionGeneration);
        return removed;
    }

    public GameSession? Get(long playerId)
    {
        _sessions.TryGetValue(playerId, out GameSession? session);
        return session;
    }
}
