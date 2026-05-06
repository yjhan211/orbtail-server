using System.Collections.Concurrent;

namespace game_server.services;

/// <summary>
///     운영툴(ops_server) 진행 로그용 인스턴스별 이벤트 버퍼.
///     매칭 단위로 최근 N개의 게임 진행 이벤트를 보관(이동/자원 변동/미션 진행/탈락/상호작용).
///     실시간 디버깅 + 영상 시연 중 진행 흐름 모니터링이 목적이다.
///     영속성 없음(서버 재시작 시 휘발).
/// </summary>
public class GameEventLogManager
{
    /// <summary>매칭당 보관할 최근 이벤트 개수.</summary>
    private const int MaxEventsPerMatching = 200;

    private readonly ConcurrentDictionary<long, MatchingEventLog> _logs = new();
    private long _nextSeq;

    public void LogMove(long matchingId, long playerId, string fromArea, string toArea, bool isBot)
    {
        Append(matchingId, "MOVE", playerId, isBot, $"{fromArea} → {toArea}");
    }

    public void LogResource(long matchingId, long playerId, int staminaDelta, int corruptionDelta,
        int stamina, int corruption, bool staminaConverted, string reason, bool isBot)
    {
        var parts = new List<string>();
        if (staminaDelta != 0) parts.Add($"체력{(staminaDelta >= 0 ? "+" : "")}{staminaDelta}");
        if (corruptionDelta != 0) parts.Add($"오염{(corruptionDelta >= 0 ? "+" : "")}{corruptionDelta}");
        parts.Add($"(체력 {stamina}/오염 {corruption})");
        if (staminaConverted) parts.Add("[전환]");
        if (!string.IsNullOrEmpty(reason)) parts.Add($"<{reason}>");
        Append(matchingId, "RESOURCE", playerId, isBot, string.Join(" ", parts));
    }

    public void LogMission(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "MISSION", playerId, isBot, description);
    }

    public void LogElimination(long matchingId, long playerId, string reason, bool isBot)
    {
        Append(matchingId, "ELIMINATE", playerId, isBot, reason);
    }

    public void LogInteraction(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "INTERACT", playerId, isBot, description);
    }

    public void LogClosure(long matchingId, string area)
    {
        Append(matchingId, "CLOSURE", 0, false, $"구역 폐쇄: {area}");
    }

    public void LogSystem(long matchingId, string description)
    {
        Append(matchingId, "SYSTEM", 0, false, description);
    }

    /// <summary>최신 이벤트가 위에 오도록 역순으로 반환 (최대 limit개).</summary>
    public List<GameEventEntry> GetRecent(long matchingId, int limit = MaxEventsPerMatching, long? sinceSeq = null)
    {
        if (!_logs.TryGetValue(matchingId, out var log)) return new List<GameEventEntry>();
        return log.Snapshot(limit, sinceSeq);
    }

    /// <summary>인스턴스 종료 시 호출하여 메모리 정리.</summary>
    public void Clear(long matchingId)
    {
        _logs.TryRemove(matchingId, out _);
    }

    private void Append(long matchingId, string type, long playerId, bool isBot, string description)
    {
        var entry = new GameEventEntry
        {
            Seq = Interlocked.Increment(ref _nextSeq),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = type,
            PlayerId = playerId,
            IsBot = isBot,
            Description = description
        };
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        log.Add(entry);
    }

    private class MatchingEventLog
    {
        private readonly LinkedList<GameEventEntry> _entries = new();
        private readonly object _lock = new();

        public void Add(GameEventEntry entry)
        {
            lock (_lock)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEventsPerMatching) _entries.RemoveFirst();
            }
        }

        public List<GameEventEntry> Snapshot(int limit, long? sinceSeq)
        {
            lock (_lock)
            {
                IEnumerable<GameEventEntry> q = _entries;
                if (sinceSeq.HasValue) q = q.Where(e => e.Seq > sinceSeq.Value);
                return q.Reverse().Take(limit).ToList();
            }
        }
    }
}

/// <summary>운영툴 진행 로그 단일 이벤트.</summary>
public class GameEventEntry
{
    public long Seq { get; set; }
    public long TimestampUnixMs { get; set; }
    public string Type { get; set; } = "";
    public long PlayerId { get; set; }
    public bool IsBot { get; set; }
    public string Description { get; set; } = "";
}
