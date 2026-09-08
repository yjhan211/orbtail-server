using game_server.services;
using MatchingEventLog = game_server.services.GameEventLogManager.MatchingEventLog;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace game_server.matches;

/// <summary>
///     종료된 최근 50개 매치의 이벤트 기록을 보관한다.
///     매치 저장소와 기록기가 이 보관함을 함께 사용하므로 서로를 생성할 필요가 없다.
///     보관 전환은 매치 잠금 안에서 호출하며, 여러 매치의 보관 목록은 자체 잠금으로 보호한다.
/// </summary>
internal sealed class MatchEventArchive
{
    private const int MaxArchivedMatchings = 50;
    private readonly ConcurrentDictionary<long, MatchingEventLog> _archivedLogs = new();
    private readonly Queue<long> _archivedMatchingIds = new();
    private readonly object _archiveLock = new();

    public bool TryGet(long matchingId, [NotNullWhen(true)] out MatchingEventLog? log) =>
        _archivedLogs.TryGetValue(matchingId, out log);

    public void Remove(long matchingId) => _archivedLogs.TryRemove(matchingId, out _);
    public void Archive(long matchingId, MatchEventLogState? state)
    {
        if (state == null || Interlocked.Exchange(ref state.Archived, 1) != 0) return;
        var log = Interlocked.Exchange(ref state.Log, null);
        state.Combat = null;
        state.Telemetry = null;
        if (log != null)
        {
            log.CompactForArchive();
            _archivedLogs[matchingId] = log;
            lock (_archiveLock)
            {
                _archivedMatchingIds.Enqueue(matchingId);
                while (_archivedMatchingIds.Count > MaxArchivedMatchings)
                {
                    long removedMatchingId = _archivedMatchingIds.Dequeue();
                    _archivedLogs.TryRemove(removedMatchingId, out _);
                }
            }
        }
    }
}
