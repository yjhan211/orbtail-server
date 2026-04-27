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
    // 폐쇄 구역 체류 페널티는 GameServer.ClosedAreaStaminaPenaltyPerTick에서 처리

    // 폐쇄 불가 (6구역): 1층 전체(행정실/1층복도/교무실/강당) + 외부(창고/운동장)
    // 폐쇄 대상 (9구역): 2~4층 전체
    //
    // [복도 hard 후순위 규칙, GDD §2.1.5, v0.0.8→v0.1.0 hard 강화, 패키지 M1A, #24]
    // 복도(2층/3층/4층복도) 3개는 말단 6구역(교실4/방송실/교실3/고사실/교실2/도서관)이
    // 모두 폐쇄된 이후에만 셔플 후보로 진입한다. 이 순서를 보장하기 위해
    // InitializeMatching()에서 말단 6구역을 앞에, 복도 3개를 뒤에 배치한 후 각 그룹 내부만 셔플.
    //
    // 이유: v0.1.0 트리 구조에서 복도 1개 폐쇄 = 척추 절단 → 직책 미션 데드락 발생 (8/8 직책 영향).
    //       말단 6구역 폐쇄(~18분) 이후에만 복도 폐쇄 허용 → 미션 페이즈 전구간 데드락 0 보장.

    // 말단 6구역 (hard 선순위 — 복도보다 먼저 폐쇄됨)
    private static readonly AreaType[] LeafClosableAreas =
    {
        AreaType.Classroom4,    // 교실4 (4층)
        AreaType.BroadcastRoom, // 방송실 (4층)
        AreaType.Classroom3,    // 교실3 (3층)
        AreaType.ExamRoom,      // 고사실 (3층)
        AreaType.Classroom2,    // 교실2 (2층)
        AreaType.Library,       // 도서관 (2층)
    };

    // 복도 3개 (hard 후순위 — 말단 6구역 전부 폐쇄 후에만 셔플 후보)
    private static readonly AreaType[] CorridorClosableAreas =
    {
        AreaType.Corridor4F,    // 4층복도
        AreaType.Corridor3F,    // 3층복도
        AreaType.Corridor2F,    // 2층복도
    };

    // matchingId → ClosureState
    private readonly ConcurrentDictionary<long, MatchingClosureState> _states = new();
    private readonly ILogger _logger;

    public AreaClosureManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     매칭 시작 시 폐쇄 스케줄 생성.
    ///     복도 hard 후순위 규칙(GDD §2.1.5): 말단 6구역을 셔플 후 앞에, 복도 3개를 셔플 후 뒤에 배치.
    ///     이 순서로 폐쇄가 진행되므로 복도는 말단 구역 전부 폐쇄(~18분) 이후에만 폐쇄됨.
    /// </summary>
    public MatchingClosureState InitializeMatching(long matchingId)
    {
        var rng = Random.Shared;
        // 말단 6구역 내부 셔플 → 복도 3개 내부 셔플 → 순서대로 결합 (hard 후순위)
        var shuffledLeaves = LeafClosableAreas.OrderBy(_ => rng.Next()).ToList();
        var shuffledCorridors = CorridorClosableAreas.OrderBy(_ => rng.Next()).ToList();
        var shuffled = shuffledLeaves.Concat(shuffledCorridors).ToList();

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
    ///     매칭 상태 조회 (게임 시작 시각 등)
    /// </summary>
    public MatchingClosureState? GetMatchingState(long matchingId)
    {
        _states.TryGetValue(matchingId, out var state);
        return state;
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
