using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.network;

/// <summary>
///     이 GameServer의 현재 플레이어 세션과 매치별 세션 목록을 함께 관리한다.
///     등록·교체·제거는 같은 잠금 안에서 두 색인에 반영하고 조회 결과는 스냅샷으로 반환한다.
///     교체된 이전 세션은 반환만 하며, 연결 종료는 호출자가 잠금 밖에서 처리한다.
/// </summary>
public sealed class GameSessionRegistry(ILogger<GameSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<long, GameClientSession> _sessionsByPlayer = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, GameClientSession>> _sessionsByMatch =
        new();
    private readonly object _mutationGate = new();

    /// <summary>
    ///     현재 세션을 등록하고 교체된 이전 세션을 반환한다. 신규·동일 세션 등록이면 null을 반환한다.
    /// </summary>
    public GameClientSession? Register(long playerId, GameClientSession session)
    {
        lock (_mutationGate)
        {
            if (!_sessionsByPlayer.TryGetValue(playerId, out GameClientSession? existingSession))
            {
                _sessionsByPlayer[playerId] = session;
                AddToMatchIndex(playerId, session);
                logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
                return null;
            }

            if (ReferenceEquals(existingSession, session))
                return null;

            _sessionsByPlayer[playerId] = session;
            RemoveFromMatchIndex(existingSession);
            AddToMatchIndex(playerId, session);
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
                RemoveFromMatchIndex(session);
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

    public List<GameClientSession> GetByMatch(long matchingId) =>
        _sessionsByMatch.TryGetValue(matchingId, out ConcurrentDictionary<long, GameClientSession>? bucket)
            ? bucket.Values.ToList()
            : [];

    public bool HasSessions(long matchingId) =>
        _sessionsByMatch.TryGetValue(matchingId, out ConcurrentDictionary<long, GameClientSession>? bucket) &&
        !bucket.IsEmpty;

    public List<GameClientSession> GetByInstance(MapId mapId, long mapSubId)
    {
        if (!_sessionsByMatch.TryGetValue(
                mapSubId,
                out ConcurrentDictionary<long, GameClientSession>? bucket))
        {
            return [];
        }

        return bucket.Values
            .Where(session => session.CurrentMapId == mapId)
            .ToList();
    }


    /// <summary>
    ///     Drops the match mirror after the match runtime has won its terminal transition.
    /// </summary>
    public void RemoveMatch(long matchingId)
    {
        lock (_mutationGate)
            _sessionsByMatch.TryRemove(matchingId, out _);
    }

    private void AddToMatchIndex(long playerId, GameClientSession session)
    {
        if (session.MatchingId <= 0)
            return;

        _sessionsByMatch
            .GetOrAdd(session.MatchingId, _ => new ConcurrentDictionary<long, GameClientSession>())
            [playerId] = session;
    }

    private void RemoveFromMatchIndex(GameClientSession session)
    {
        if (!session.PlayerId.HasValue)
            return;
        if (!_sessionsByMatch.TryGetValue(
                session.MatchingId,
                out ConcurrentDictionary<long, GameClientSession>? bucket))
        {
            return;
        }

        ((ICollection<KeyValuePair<long, GameClientSession>>)bucket)
            .Remove(new KeyValuePair<long, GameClientSession>(session.PlayerId.Value, session));
    }
}
