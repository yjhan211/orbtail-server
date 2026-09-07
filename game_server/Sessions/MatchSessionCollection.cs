using network.common;

namespace game_server.sessions;

/// <summary>
///     매치 하나에 등록된 사람 세션을 관리한다. 봇과 아직 접속하지 않은 참가자는 포함하지 않는다.
///     목록 잠금 안에서는 복사·등록·제거만 한다. 세션 콜백이나 다른 매치의 잠금은 호출하지 않는다.
///     매치 종료 시 목록을 닫지만 TCP 연결과 서버 전체 세션 색인은 별도로 정리한다.
/// </summary>
internal sealed class MatchSessionCollection
{
    private readonly object _sessionsLock = new();
    private readonly Dictionary<long, GameClientSession> _sessions = new();
    private bool _closed;

    internal void Add(long playerId, GameClientSession session)
    {
        lock (_sessionsLock)
        {
            if (_closed)
                throw new InvalidOperationException("Cannot register a session in a closed match.");
            _sessions[playerId] = session;
        }
    }

    internal void Remove(long playerId, GameClientSession session)
    {
        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(playerId, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(playerId);
        }
    }

    public bool HasSessions
    {
        get
        {
            lock (_sessionsLock)
                return _sessions.Count != 0;
        }
    }

    public List<GameClientSession> Snapshot()
    {
        lock (_sessionsLock)
            return _sessions.Values.ToList();
    }

    /// <summary>같은 구역의 생존 세션을 고른다. 구역·탈락 상태의 일관성이 필요하면 매치 잠금 안에서 호출한다.</summary>
    public List<GameClientSession> GetInArea(AreaType area, long? excludePlayerId = null) =>
        FilterInArea(Snapshot(), area, excludePlayerId);

    internal static List<GameClientSession> FilterInArea(
        IEnumerable<GameClientSession> snapshot, AreaType area, long? excludePlayerId = null) =>
        snapshot.Where(session => !session.IsEliminated && session.CurrentArea == area &&
            (!excludePlayerId.HasValue || session.PlayerId != excludePlayerId)).ToList();

    internal void Close()
    {
        lock (_sessionsLock)
        {
            _closed = true;
            _sessions.Clear();
        }
    }
}
