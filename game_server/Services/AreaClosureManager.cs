using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.services;

/// <summary>
///     인스턴스별 구역 폐쇄 관리.
///     시간 경과에 따른 순차 폐쇄, 경고, 폐쇄 구역 진입 시 페널티.
/// </summary>
public class AreaClosureManager
{
    private const int ClosureWarningSeconds = 30;   // 폐쇄 전 경고 시간
    private const int ClosureIntervalSeconds = 180; // 구역 간 폐쇄 간격 (3분)
    private const int FirstClosureDelaySeconds = 300; // 첫 폐쇄까지 딜레이 (5분)
    private const int ClosedAreaStaminaPenalty = 20; // 폐쇄 구역 진입 시 스태미나 감소

    // 폐쇄 대상 구역 (복도, 강당 등 핵심 구역은 제외)
    private static readonly AreaType[] ClosableAreas =
    {
        AreaType.Classroom1,    // 고사실
        AreaType.Classroom2,    // 방송실
        AreaType.Classroom3,    // 보건실
        AreaType.Classroom4,    // 3-1
        AreaType.Classroom5,    // 3-2
        AreaType.Storage1,      // 창고 A
        AreaType.Storage2,      // 창고 B
        AreaType.AdminOffice1,  // 행정실
        AreaType.AdminOffice2,  // 교무실
        AreaType.Terrace1,      // 쓰레기장 A
        AreaType.Terrace2,      // 쓰레기장 B
    };

    // matchingId → ClosureState
    private readonly ConcurrentDictionary<long, MatchingClosureState> _states = new();
    private readonly ILogger _logger;

    public AreaClosureManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     매칭 시작 시 폐쇄 스케줄 생성 (무작위 순서)
    /// </summary>
    public MatchingClosureState InitializeMatching(long matchingId)
    {
        var rng = Random.Shared;
        var shuffled = ClosableAreas.OrderBy(_ => rng.Next()).ToList();

        var state = new MatchingClosureState
        {
            MatchingId = matchingId,
            ClosureOrder = shuffled,
            ClosedAreas = new HashSet<AreaType>(),
            NextClosureIndex = 0,
            GameStartTime = DateTime.UtcNow
        };

        _states[matchingId] = state;

        _logger.LogInformation("구역 폐쇄 스케줄 생성: MatchingId={MatchingId}, 순서={Order}",
            matchingId, string.Join("→", shuffled));

        return state;
    }

    /// <summary>
    ///     현재 시각 기준 폐쇄해야 할 구역 확인.
    ///     반환: (경고할 구역, 폐쇄 확정할 구역)
    /// </summary>
    public (AreaType? warningArea, AreaType? closingArea) CheckClosureSchedule(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return (null, null);
        if (state.NextClosureIndex >= state.ClosureOrder.Count) return (null, null);

        double elapsed = (DateTime.UtcNow - state.GameStartTime).TotalSeconds;
        double nextClosureTime = FirstClosureDelaySeconds + state.NextClosureIndex * ClosureIntervalSeconds;
        double warningTime = nextClosureTime - ClosureWarningSeconds;

        AreaType? warningArea = null;
        AreaType? closingArea = null;

        // 폐쇄 시간 도달
        if (elapsed >= nextClosureTime)
        {
            var area = state.ClosureOrder[state.NextClosureIndex];
            if (!state.ClosedAreas.Contains(area))
            {
                state.ClosedAreas.Add(area);
                closingArea = area;
                state.NextClosureIndex++;
                _logger.LogInformation("구역 폐쇄: MatchingId={MatchingId}, Area={Area}", matchingId, area);
            }
        }
        // 경고 시간 도달 (아직 경고 안 보낸 경우)
        else if (elapsed >= warningTime && !state.WarningsSent.Contains(state.NextClosureIndex))
        {
            warningArea = state.ClosureOrder[state.NextClosureIndex];
            state.WarningsSent.Add(state.NextClosureIndex);
            int remaining = (int)(nextClosureTime - elapsed);
            _logger.LogInformation("구역 폐쇄 경고: MatchingId={MatchingId}, Area={Area}, {Remaining}초 후",
                matchingId, warningArea, remaining);
        }

        return (warningArea, closingArea);
    }

    /// <summary>
    ///     해당 구역이 폐쇄되었는지 확인
    /// </summary>
    public bool IsAreaClosed(long matchingId, AreaType area)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return false;
        return state.ClosedAreas.Contains(area);
    }

    /// <summary>
    ///     폐쇄 구역 진입 시 스태미나 페널티
    /// </summary>
    public int GetClosedAreaPenalty() => ClosedAreaStaminaPenalty;

    /// <summary>
    ///     매칭 정리
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _states.TryRemove(matchingId, out _);
    }
}

public class MatchingClosureState
{
    public long MatchingId { get; set; }
    public List<AreaType> ClosureOrder { get; set; } = new();
    public HashSet<AreaType> ClosedAreas { get; set; } = new();
    public int NextClosureIndex { get; set; }
    public DateTime GameStartTime { get; set; }
    public HashSet<int> WarningsSent { get; set; } = new(); // 경고 보낸 인덱스
}
