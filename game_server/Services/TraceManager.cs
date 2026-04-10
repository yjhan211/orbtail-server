using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

/// <summary>
///     인스턴스별 흔적 저장/발견 관리.
///     미션 완료 시 자동 생성되는 미션 흔적과 마니또가 배치하는 흔적을 모두 관리.
///     흔적은 게임 끝까지 유지 (GDD 확정).
///     발견 방식: 오브젝트 탐색 시 해당 오브젝트에 흔적이 있으면 발견 (자동 발견 X).
/// </summary>
public class TraceManager
{
    // matchingId → (interactId → 흔적 목록)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<int, List<StoredTrace>>> _traces = new();
    private static long _nextTraceId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    ///     흔적 저장 (미션 완료 또는 마니또 배치 시)
    /// </summary>
    public StoredTrace AddTrace(long matchingId, AreaType area, int interactId,
        string description, long placedByPlayerId, bool isMissionTrace)
    {
        var matchingTraces = _traces.GetOrAdd(matchingId, _ => new ConcurrentDictionary<int, List<StoredTrace>>());
        var objectTraces = matchingTraces.GetOrAdd(interactId, _ => new List<StoredTrace>());

        var trace = new StoredTrace
        {
            TraceId = Interlocked.Increment(ref _nextTraceId),
            AreaType = area,
            InteractId = interactId,
            Description = description,
            PlacedByPlayerId = placedByPlayerId,
            IsMissionTrace = isMissionTrace
        };

        lock (objectTraces)
        {
            objectTraces.Add(trace);
        }

        return trace;
    }

    /// <summary>
    ///     특정 오브젝트의 미발견 흔적 조회 (탐색 시 발견 대상)
    /// </summary>
    public List<StoredTrace> GetUndiscoveredTraces(long matchingId, int interactId, long discovererId)
    {
        if (!_traces.TryGetValue(matchingId, out var matchingTraces)) return new List<StoredTrace>();
        if (!matchingTraces.TryGetValue(interactId, out var objectTraces)) return new List<StoredTrace>();

        lock (objectTraces)
        {
            return objectTraces
                .Where(t => !t.DiscoveredByPlayerIds.Contains(discovererId) && t.PlacedByPlayerId != discovererId)
                .ToList();
        }
    }

    /// <summary>
    ///     흔적 발견 처리 (발견자 기록)
    /// </summary>
    public void MarkDiscovered(long matchingId, int interactId, long discovererId, long traceId)
    {
        if (!_traces.TryGetValue(matchingId, out var matchingTraces)) return;
        if (!matchingTraces.TryGetValue(interactId, out var objectTraces)) return;

        lock (objectTraces)
        {
            var trace = objectTraces.FirstOrDefault(t => t.TraceId == traceId);
            trace?.DiscoveredByPlayerIds.Add(discovererId);
        }
    }

    /// <summary>
    ///     매칭 정리
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _traces.TryRemove(matchingId, out _);
    }
}

public class StoredTrace
{
    public long TraceId { get; set; }
    public AreaType AreaType { get; set; }
    public int InteractId { get; set; }
    public string Description { get; set; } = "";
    public long PlacedByPlayerId { get; set; }
    public bool IsMissionTrace { get; set; }
    public HashSet<long> DiscoveredByPlayerIds { get; set; } = new();
}
