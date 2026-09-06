using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace user_server.sessions;

/// <summary>
///     이 UserServer에 접속한 플레이어의 현재 세션을 플레이어 ID별로 보관한다.
///     등록 시 로그인 세대 번호를 비교해 더 최신 세션으로 교체하며,
///     교체된 이전 세션은 호출자가 연결을 끊을 수 있도록 반환한다.
///
///     제거 시에는 현재 등록된 세션이 요청한 세션과 같은 경우에만 삭제한다.
///     이전 연결의 종료 처리가 새 세션을 목록에서 지우지 않도록 하기 위함이다.
/// </summary>
internal sealed class PlayerSessionRegistry(ILogger<PlayerSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<long, PlayerSession> _sessions = new();

    public (bool Accepted, PlayerSession? PreviousSession) Register(long playerId, PlayerSession session)
    {
        if (session.SessionGeneration <= 0)
            throw new InvalidOperationException("A session must own a positive generation before registration.");

        while (true)
        {
            if (!_sessions.TryGetValue(playerId, out var existingSession))
            {
                if (_sessions.TryAdd(playerId, session))
                {
                    logger.LogInformation("Session registered: PlayerId={PlayerId}, Generation={Generation}", playerId, session.SessionGeneration);
                    return (true, null);
                }
                continue;
            }

            if (ReferenceEquals(existingSession, session))
                return (true, null);

            if (existingSession.SessionGeneration >= session.SessionGeneration)
            {
                logger.LogWarning("Stale session registration rejected: PlayerId={PlayerId}, Generation={Generation}, CurrentGeneration={CurrentGeneration}",
                    playerId, session.SessionGeneration, existingSession.SessionGeneration);
                return (false, null);
            }

            if (!_sessions.TryUpdate(playerId, session, existingSession))
                continue;

            logger.LogWarning("Session replaced after duplicate login: PlayerId={PlayerId}, Generation={Generation}, PreviousGeneration={PreviousGeneration}",
                playerId, session.SessionGeneration, existingSession.SessionGeneration);
            return (true, existingSession);
        }
    }

    public bool Remove(long playerId, PlayerSession session)
    {
        var entry = new KeyValuePair<long, PlayerSession>(playerId, session);
        ICollection<KeyValuePair<long, PlayerSession>> entries = _sessions;
        bool removed = entries.Remove(entry);
        if (removed)
            logger.LogInformation("Session removed: PlayerId={PlayerId}, Generation={Generation}", playerId, session.SessionGeneration);
        return removed;
    }

    public PlayerSession? Get(long playerId)
    {
        _sessions.TryGetValue(playerId, out var session);
        return session;
    }
}
