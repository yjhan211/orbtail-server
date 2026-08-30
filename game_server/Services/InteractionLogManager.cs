using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

/// <summary>
///     인스턴스별 1:1 상호작용 로그 기록.
///     각 플레이어가 누구와 만나 무슨 직책을 주장했는지 추적.
///     이후 선택지 생성 시 교차 검증용 데이터 제공.
/// </summary>
public class InteractionLogManager
{
    // matchingId → (playerId → 로그 목록)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, List<InteractionLogEntry>>> _logs = new();

    /// <summary>
    ///     상호작용 로그 기록
    /// </summary>
    public void AddLog(long matchingId, long myPlayerId, long otherPlayerId,
        JobTitle claimedJob, AreaType area)
    {
        var matchingLogs = _logs.GetOrAdd(matchingId, _ => new ConcurrentDictionary<long, List<InteractionLogEntry>>());
        var playerLogs = matchingLogs.GetOrAdd(myPlayerId, _ => new List<InteractionLogEntry>());

        lock (playerLogs)
        {
            playerLogs.Add(new InteractionLogEntry
            {
                OtherPlayerId = otherPlayerId,
                ClaimedJobTitle = claimedJob,
                Area = area,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    ///     동일 직책 주장 충돌 감지: 2명 이상이 같은 직책 주장
    /// </summary>
    public List<(JobTitle job, List<long> claimers)> DetectDuplicateClaims(long matchingId)
    {
        var results = new List<(JobTitle, List<long>)>();
        if (!_logs.TryGetValue(matchingId, out var matchingLogs)) return results;

        // 각 플레이어가 가장 많이 주장한 직책 수집
        var primaryClaims = new Dictionary<long, JobTitle>();

        foreach (var (_, playerLogs) in matchingLogs)
        {
            lock (playerLogs)
            {
                foreach (var log in playerLogs)
                {
                    // 가장 최근 주장 사용
                    primaryClaims[log.OtherPlayerId] = log.ClaimedJobTitle;
                }
            }
        }

        // 같은 직책을 주장하는 플레이어 그룹 찾기
        var grouped = primaryClaims.GroupBy(kv => kv.Value)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, g.Select(kv => kv.Key).ToList()))
            .ToList();

        return grouped;
    }

    public void CleanupMatching(long matchingId)
    {
        _logs.TryRemove(matchingId, out _);
    }
}

public class InteractionLogEntry
{
    public long OtherPlayerId { get; set; }
    public JobTitle ClaimedJobTitle { get; set; }
    public AreaType Area { get; set; }
    public DateTime Timestamp { get; set; }
}
