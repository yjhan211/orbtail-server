using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.network;

/// <summary>
///     Owns the process-local current player-to-session registry and its match-scoped mirror index.
///     Mutations are serialized so replacement and reference-equal removal update both indexes in one mutation boundary;
///     readers receive snapshots that are safe to enumerate without holding the mutation gate.
/// </summary>
internal sealed class GameSessionRegistry
{
    private readonly ConcurrentDictionary<long, GameClientSession> _sessionsByPlayer = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, GameClientSession>> _sessionsByMatch =
        new();
    private readonly object _mutationGate = new();

    /// <summary>
    ///     Registers a session as the current connection for a player and returns the session it superseded.
    /// </summary>
    public GameClientSession? Register(long playerId, GameClientSession session, out bool added)
    {
        lock (_mutationGate)
        {
            if (!_sessionsByPlayer.TryGetValue(playerId, out GameClientSession? existingSession))
            {
                _sessionsByPlayer[playerId] = session;
                AddToMatchIndex(playerId, session);
                added = true;
                return null;
            }

            added = false;
            if (ReferenceEquals(existingSession, session))
                return null;

            _sessionsByPlayer[playerId] = session;
            RemoveFromMatchIndex(existingSession);
            AddToMatchIndex(playerId, session);
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

    public IReadOnlyCollection<long> GetActiveMatchingIds() =>
        _sessionsByMatch
            .Where(pair => !pair.Value.IsEmpty)
            .Select(pair => pair.Key)
            .ToArray();

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
        if (session.CurrentMapSubId <= 0)
            return;

        _sessionsByMatch
            .GetOrAdd(session.CurrentMapSubId, _ => new ConcurrentDictionary<long, GameClientSession>())
            [playerId] = session;
    }

    private void RemoveFromMatchIndex(GameClientSession session)
    {
        if (!session.PlayerId.HasValue)
            return;
        if (!_sessionsByMatch.TryGetValue(
                session.CurrentMapSubId,
                out ConcurrentDictionary<long, GameClientSession>? bucket))
        {
            return;
        }

        ((ICollection<KeyValuePair<long, GameClientSession>>)bucket)
            .Remove(new KeyValuePair<long, GameClientSession>(session.PlayerId.Value, session));
    }
}
