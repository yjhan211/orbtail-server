using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.helpers;

namespace game_server.services;

/// <summary>
///     인스턴스별 구역 폐쇄 관리.
///     시간 경과에 따른 순차 폐쇄, 경고, 폐쇄 구역 진입 시 페널티.
/// </summary>
public class AreaClosureManager
{
    private const int ClosureWarningSeconds = 30;   // 폐쇄 전 경고 시간
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
    private readonly MatchingConfigService _matchingConfig;

    public AreaClosureManager(ILogger logger, MatchingConfigService matchingConfig)
    {
        _logger = logger;
        _matchingConfig = matchingConfig;
    }

    /// <summary>
    ///     매칭 시작 시 폐쇄 스케줄 생성.
    ///     MatchingConfigService에서 config를 읽어 적용한다.
    ///     forcedSequence가 null이면 기존 무작위 규칙(복도 hard 후순위 GDD §2.1.5) 사용.
    ///     복도 hard 후순위 규칙: 말단 6구역을 셔플 후 앞에, 복도 3개를 셔플 후 뒤에 배치.
    ///
    ///     #87 추가 규칙 (jobsInMatching 제공 시):
    ///     - 1번째 슬롯(시작 5분)은 이번 매칭 직책들의 1단계(Material) 발견 구역 제외 강제 —
    ///       race 자동 패배 차단.
    ///     - CL(미화부원, 6) 강당 출구 후순위 (CL 과강화 보정).
    /// </summary>
    public MatchingClosureState InitializeMatching(long matchingId, List<JobTitle>? jobsInMatching = null)
    {
        var config = _matchingConfig.GetClosureConfig();
        // H1 결정론 시드 — DemoMode 활성화 시 시드 기반 RNG.
        var rng = DemoMode.IsActive ? new Random(DemoMode.Seed) : Random.Shared;

        List<AreaType> sequence;
        if (DemoMode.IsActive)
        {
            // H2 폐쇄 셔플 보호 — 도서관/교실2 제외한 시연용 강제 시퀀스.
            sequence = DemoMode.ForcedClosureSequence
                .Where(a => !DemoMode.ProtectedAreas.Contains(a))
                .ToList();
        }
        else if (config.ForcedSequence != null && config.ForcedSequence.Count > 0)
        {
            // 강제 시퀀스 사용 (그대로 적용)
            sequence = config.ForcedSequence;
        }
        else
        {
            // 기존 무작위 로직 — 복도 hard 후순위 규칙
            var shuffledLeaves = LeafClosableAreas.OrderBy(_ => rng.Next()).ToList();
            var shuffledCorridors = CorridorClosableAreas.OrderBy(_ => rng.Next()).ToList();
            sequence = shuffledLeaves.Concat(shuffledCorridors).ToList();

            // #87: 직책 풀에 따른 셔플 우선순위 보정
            if (jobsInMatching != null && jobsInMatching.Count > 0)
                sequence = ApplyJobAwareShuffle(sequence, jobsInMatching);
        }

        var state = new MatchingClosureState
        {
            MatchingId = matchingId,
            ClosureOrder = sequence,
            ClosedAreas = new HashSet<AreaType>(),
            NextClosureIndex = 0,
            GameStartTime = DateTime.UtcNow,
            StartDelaySec = config.StartDelaySec,
            IntervalSec = config.IntervalSec
        };

        _states[matchingId] = state;

        _logger.LogInformation(
            "구역 폐쇄 스케줄 생성: MatchingId={MatchingId}, startDelay={StartDelay}s, interval={Interval}s, 순서={Order}",
            matchingId, config.StartDelaySec, config.IntervalSec, string.Join("→", sequence));

        return state;
    }

    /// <summary>
    ///     #87: 직책 풀 인지 셔플. 시작 5분 내 1단계 보장 + 직책별 후순위 보정.
    /// </summary>
    private static List<AreaType> ApplyJobAwareShuffle(List<AreaType> baseSequence, List<JobTitle> jobs)
    {
        var result = new List<AreaType>(baseSequence);

        // 1) 이번 매칭 모든 직책의 1단계(Material) 발견 구역 집합
        var stage1Areas = new HashSet<AreaType>();
        foreach (var job in jobs)
        {
            var materials = GameMissionData.GetMaterials((short)job);
            foreach (var part in materials)
            {
                if (part.TargetArea > 0) stage1Areas.Add((AreaType)part.TargetArea);
            }
        }

        // 첫 슬롯(시작 5분 폐쇄)이 1단계 발견 구역이면, 1단계가 아닌 area를 앞으로 swap
        if (stage1Areas.Contains(result[0]))
        {
            for (int i = 1; i < result.Count; i++)
            {
                if (stage1Areas.Contains(result[i])) continue;
                (result[0], result[i]) = (result[i], result[0]);
                break;
            }
        }

        // 2) CL(미화부원=6) 강당 출구(=강당) 후순위 — 강당은 폐쇄 불가지만,
        // 미화부원 발견 구역(교실2/2층복도 등)도 한 번 보정해 race 자동 패배를 더 차단한다.
        if (jobs.Contains(JobTitle.CLEANING_MEMBER))
            DemoteOneOfTheseAreasIfPossible(result,
                new[] { AreaType.Classroom2 });

        return result;
    }

    /// <summary>
    ///     주어진 후보 구역들 중 시퀀스에 포함된 것 1개를 가능한 한 뒤로(말단 그룹 끝쪽) 이동.
    ///     복도 hard 후순위 규칙은 깨뜨리지 않도록 말단 그룹 내부에서만 swap한다.
    /// </summary>
    private static void DemoteOneOfTheseAreasIfPossible(List<AreaType> seq, IEnumerable<AreaType> candidates)
    {
        // 말단 6구역 영역 = 인덱스 [0..5] (생성 시 leaves가 앞에 배치됨)
        const int leafGroupEnd = 6;
        foreach (var candidate in candidates)
        {
            int idx = seq.IndexOf(candidate);
            if (idx < 0 || idx >= leafGroupEnd) continue;

            int targetIdx = leafGroupEnd - 1;
            if (idx == targetIdx) return;     // 이미 말단 그룹 끝
            (seq[idx], seq[targetIdx]) = (seq[targetIdx], seq[idx]);
            return;
        }
    }

    /// <summary>
    ///     현재 시각 기준 폐쇄해야 할 구역 확인.
    ///     반환: (경고할 구역, 폐쇄 확정할 구역)
    /// </summary>
    public (AreaType? warningArea, int warningSeconds, long closureAtUnixMs, AreaType? closingArea)
        CheckClosureSchedule(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return (null, 0, 0, null);
        if (state.NextClosureIndex >= state.ClosureOrder.Count) return (null, 0, 0, null);

        double elapsed = (DateTime.UtcNow - state.GameStartTime).TotalSeconds;
        double nextClosureTime = state.StartDelaySec + state.NextClosureIndex * state.IntervalSec;
        double warningTime = nextClosureTime - ClosureWarningSeconds;

        AreaType? warningArea = null;
        AreaType? closingArea = null;
        int warningSeconds = 0;
        long closureAtUnixMs = 0;

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
            warningSeconds = Math.Max(1, (int)Math.Ceiling(nextClosureTime - elapsed));

            // 정확한 폐쇄 시각 (UTC Unix ms) — 클라이언트 카운트다운 동기화용
            var closureAtUtc = state.GameStartTime.AddSeconds(nextClosureTime);
            closureAtUnixMs = ((DateTimeOffset)closureAtUtc).ToUnixTimeMilliseconds();

            _logger.LogInformation("구역 폐쇄 경고: MatchingId={MatchingId}, Area={Area}, {Remaining}초 후 (closure at {UnixMs}ms)",
                matchingId, warningArea, warningSeconds, closureAtUnixMs);
        }

        return (warningArea, warningSeconds, closureAtUnixMs, closingArea);
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
    ///     어드민 운영툴용 폐쇄 스케줄 요약 조회.
    ///     다음 폐쇄 시각, 카운트다운, 경고 활성 여부를 계산해 반환한다.
    ///     startDelaySec / intervalSec은 이 인스턴스에 적용된 값이다.
    /// </summary>
    public (List<int> closureSequence, List<int> closedAreaIds, int nextAreaType,
        long nextAtUnix, int secondsLeft, bool warningActive, int startDelaySec, int intervalSec)
        GetClosureSnapshot(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return ([], [], -1, -1, -1, false, MatchingConfigService.DefaultStartDelaySec, MatchingConfigService.DefaultIntervalSec);

        var sequence = state.ClosureOrder.Select(a => (int)a).ToList();
        var closed = state.ClosedAreas.Select(a => (int)a).ToList();

        if (state.NextClosureIndex >= state.ClosureOrder.Count)
            return (sequence, closed, -1, -1, -1, false, state.StartDelaySec, state.IntervalSec);

        double elapsed = (DateTime.UtcNow - state.GameStartTime).TotalSeconds;
        double nextClosureTime = state.StartDelaySec + state.NextClosureIndex * state.IntervalSec;
        double remaining = nextClosureTime - elapsed;

        int nextAreaType = (int)state.ClosureOrder[state.NextClosureIndex];
        long nextAtUnix = ((DateTimeOffset)state.GameStartTime).ToUnixTimeSeconds() + (long)nextClosureTime;
        int secondsLeft = remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
        bool warningActive = remaining > 0 && remaining <= ClosureWarningSeconds;

        return (sequence, closed, nextAreaType, nextAtUnix, secondsLeft, warningActive, state.StartDelaySec, state.IntervalSec);
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

    /// <summary>이 인스턴스에 적용된 폐쇄 시작 딜레이 (초) — 생성 시 config에서 복사</summary>
    public int StartDelaySec { get; set; } = MatchingConfigService.DefaultStartDelaySec;

    /// <summary>이 인스턴스에 적용된 폐쇄 간격 (초) — 생성 시 config에서 복사</summary>
    public int IntervalSec { get; set; } = MatchingConfigService.DefaultIntervalSec;
}
