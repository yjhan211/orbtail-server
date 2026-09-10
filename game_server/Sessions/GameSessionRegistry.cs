using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace game_server.sessions;

/// <summary>
///     이 GameServer에 등록된 플레이어별 현재 세션을 관리한다.
///     세션을 등록·교체·제거할 때 해당 MatchPlayer의 세션 참조도 함께 갱신한다.
///     이전 세션의 종료 처리가 새 세션을 지우지 않도록 삭제할 때 세션 객체까지 확인한다.
/// </summary>
public sealed class GameSessionRegistry(ILogger<GameSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<long, GameClientSession> _sessionsByPlayer = new();
    private readonly object _sessionRegistrationLock = new();

    public GameClientSession? Register(long playerId, GameClientSession session)
    {
        var match = session.Match;
        using var scope = match.Enter();
        if (match.IsEnded)
        {
            throw new InvalidOperationException("Cannot register a session in a terminal match.");
        }

        if (session.PlayerId != playerId || session.MatchingId != match.MatchingId ||
            !ReferenceEquals(match.GetParticipant(playerId), session.Player))
        {
            throw new InvalidOperationException("Session identity does not match its bound match.");
        }

        lock (_sessionRegistrationLock)
        {
            if (!_sessionsByPlayer.TryGetValue(playerId, out var existingSession))
            {
                session.Player.Session = session;
                _sessionsByPlayer[playerId] = session;
                logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
                return null;
            }

            if (ReferenceEquals(existingSession, session))
            {
                return null;
            }

            session.Player.Session = session;
            _sessionsByPlayer[playerId] = session;
            existingSession.Player.DetachSession(existingSession);
            logger.LogWarning("Game client session replaced: PlayerId={PlayerId}", playerId);
            return existingSession;
        }
    }

    public bool Remove(GameClientSession session)
    {
        if (!session.PlayerId.HasValue)
        {
            return false;
        }

        lock (_sessionRegistrationLock)
        {
            bool removed =
                ((ICollection<KeyValuePair<long, GameClientSession>>)_sessionsByPlayer)
                .Remove(new KeyValuePair<long, GameClientSession>(session.PlayerId.Value, session));
            if (removed)
            {
                session.Player.DetachSession(session);
            }
            return removed;
        }
    }

    public bool TryGetSession(long playerId, out GameClientSession? session) => _sessionsByPlayer.TryGetValue(playerId, out session);

    public List<GameClientSession> GetAllSessions()
    {
        return _sessionsByPlayer.Values.ToList();
    }
}
