using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace game_server.sessions;

/// <summary>
///     이 GameServer의 플레이어별 현재 세션을 관리하고 매치 소유 목록의 등록·교체·제거를 조율한다.
///     등록 잠금 순서는 대상 매치 → 레지스트리 → 세션 목록이다. 이전 매치의 실행 잠금은 잡지 않는다.
///     교체된 이전 세션은 반환만 하며, 연결 종료는 호출자가 잠금 밖에서 처리한다.
/// </summary>
public sealed class GameSessionRegistry(ILogger<GameSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<long, GameClientSession> _sessionsByPlayer = new();
    private readonly object _mutationGate = new();

    /// <summary>
    ///     현재 세션을 등록하고 교체된 이전 세션을 반환한다. 신규·동일 세션 등록이면 null을 반환한다.
    /// </summary>
    public GameClientSession? Register(long playerId, GameClientSession session)
    {
        var match = session.Match;
        using var scope = match.Enter();
        if (match.IsTerminal)
            throw new InvalidOperationException("Cannot register a session in a terminal match.");
        if (session.PlayerId != playerId || session.MatchingId != match.MatchingId)
            throw new InvalidOperationException("Session identity does not match its bound match.");
        lock (_mutationGate)
        {
            if (!_sessionsByPlayer.TryGetValue(playerId, out GameClientSession? existingSession))
            {
                match.Sessions.Add(playerId, session);
                _sessionsByPlayer[playerId] = session;
                logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
                return null;
            }

            if (ReferenceEquals(existingSession, session))
                return null;

            match.Sessions.Add(playerId, session);
            _sessionsByPlayer[playerId] = session;
            existingSession.Match.Sessions.Remove(playerId, existingSession);
            logger.LogWarning("Game client session replaced: PlayerId={PlayerId}", playerId);
            return existingSession;
        }
    }

    /// <summary>
    ///     Removes a session only when it is still the player's current connection.
    /// </summary>
    public bool Remove(GameClientSession session)
    {
        if (!session.PlayerId.HasValue)
            return false;

        lock (_mutationGate)
        {
            bool removed =
                ((ICollection<KeyValuePair<long, GameClientSession>>)_sessionsByPlayer)
                .Remove(new KeyValuePair<long, GameClientSession>(session.PlayerId.Value, session));
            if (removed)
                session.Match.Sessions.Remove(session.PlayerId.Value, session);
            return removed;
        }
    }

    public bool TryGetCurrent(long playerId, out GameClientSession? session) =>
        _sessionsByPlayer.TryGetValue(playerId, out session);

    public List<GameClientSession> SnapshotAll() => SnapshotWhere(static _ => true);

    public List<GameClientSession> SnapshotWhere(Func<GameClientSession, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var snapshot = new List<GameClientSession>();
        foreach (KeyValuePair<long, GameClientSession> pair in _sessionsByPlayer)
        {
            if (predicate(pair.Value))
                snapshot.Add(pair.Value);
        }

        return snapshot;
    }

}
