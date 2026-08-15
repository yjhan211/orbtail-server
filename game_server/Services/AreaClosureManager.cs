using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
/// Survivor Royale P0의 서버 권위 구역 폐쇄·오버타임 상태를 관리한다.
/// 복도는 모든 방 폐쇄가 끝난 뒤 마지막 웨이브에서만 폐쇄한다.
/// </summary>
public class AreaClosureManager
{
    public const int ClosureWarningSeconds = 15;
    public const int ResourceTickSeconds = 5;

    // #219 폐쇄 부활 (2026-08-08): 클론 맵 기준 — 바깥 포드(교실1·교실2·창고·보건실) →
    // 중간 포드(고사실·방송실·행정실·교무실) → 쌍 구역(도서관·강당) → 밴드(테라스·복도) 순.
    // 2026-08-09: 오염 3배 + 운동장 최종 폐쇄 — 종반엔 어디도 안전하지 않다.
    // 2026-08-10 M3-2: 매치 길이(SWARM_MATCH_DURATION_SECONDS)에 맞춰 압축 —
    // 운동장 최종 폐쇄 = 타이머 만료 = 승리 판정이 한 시점에 겹친다.
    // 오염 재조정 (#222 08-10 2차): ×3은 한 틱 360~540 — 즉사 아니면 빈 바 생존만 남는
    // 이분법이었다. 5초 틱 기준 첫 웨이브 5방(85), 종반 3방(145) 사망으로 완만화 —
    // 여전히 "즉시 나가야 하는" 압박이되 체력바가 단계적으로 읽힌다.
    // #226 단계 B: 5분(300초) 오브 점수전으로 재정렬 — 공급 축소(1:30/3:00/4:00)와 맞물려
    // 마지막 60초는 신규 스폰 없이 절단·순위 역전만 남는다.
    private static readonly IReadOnlyList<ClosureWaveDefinition> DefaultP0Waves =
    [
        new(100, [AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2], 17),
        new(150, [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.AdminOffice, AreaType.StaffRoom], 20),
        new(200, [AreaType.Library, AreaType.Gym], 23),
        new(250, [AreaType.Corridor, AreaType.Junkyard], 26),
        new(300, [AreaType.Ground], 29)
    ];

    // 순차 폐쇄 (#229): 한 웨이브가 네 구역을 동시에 닫으면 "문이 한꺼번에 내려온" 한 순간만
    // 남고 어디로 갈지 고르는 시간이 사라진다. 구역을 하나씩 쪼개 원래 시각보다 앞당겨 흩는다 —
    // 마지막 구역은 원래 시각 그대로라 매치 종료 봉투(최종 운동장 폐쇄 = 타이머 만료)는 안 밀린다.
    // 순서는 매치마다 섞어 어떤 방이 일찍 닫힐지 미리 알 수 없게 한다.
    private const int ClosureStaggerStepSeconds = 4;

    private static List<ClosureWaveDefinition> StaggerWaveAreas(List<ClosureWaveDefinition> waves)
    {
        var rng = new Random();
        var staggered = new List<ClosureWaveDefinition>();
        foreach (var wave in waves)
        {
            if (wave.Areas.Count <= 1)
            {
                staggered.Add(wave);
                continue;
            }

            var shuffled = wave.Areas.OrderBy(_ => rng.Next()).ToList();
            for (int index = 0; index < shuffled.Count; index++)
            {
                // 마지막(index = Count-1)이 원래 시각, 앞선 것들이 그만큼 일찍.
                int offset = (shuffled.Count - 1 - index) * ClosureStaggerStepSeconds;
                staggered.Add(wave with
                {
                    ClosureAtSeconds = wave.ClosureAtSeconds - offset,
                    Areas = [shuffled[index]]
                });
            }
        }

        return staggered.OrderBy(wave => wave.ClosureAtSeconds).ToList();
    }

    private readonly ConcurrentDictionary<long, MatchingClosureState> _states = new();
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;

    public AreaClosureManager(ILogger logger, MatchingConfigService matchingConfig, Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _ = matchingConfig; // Legacy admin config is intentionally not used by fixed P0 waves.
    }

    /// <summary>
    /// 매치별 고정 P0 웨이브를 만든다. jobsInMatching은 기존 호출 호환을 위해 유지한다.
    /// </summary>
    public MatchingClosureState InitializeMatching(
        long matchingId,
        List<JobTitle>? jobsInMatching = null,
        IEnumerable<AreaType>? initiallyOpenAreas = null,
        IReadOnlyList<ClosureWaveDefinition>? wavesOverride = null)
    {
        _ = jobsInMatching;

        // 동일 매치의 뒤늦은 접속(재접속 포함)이 폐쇄 시계와 누적 폐쇄 상태를
        // 처음부터 다시 만들면 안 된다. 최초 접속만 상태를 생성한다.
        if (_states.TryGetValue(matchingId, out var existingState))
            return existingState;

        var mapAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .ToHashSet();
        var waves = (wavesOverride ?? DefaultP0Waves)
            .Select(wave => wave with
            {
                Areas = wave.Areas.Where(mapAreas.Contains).ToArray()
            })
            .Where(wave => wave.Areas.Count > 0)
            .ToList();
        if (wavesOverride == null)
            waves = StaggerWaveAreas(waves);

        var state = new MatchingClosureState
        {
            MatchingId = matchingId,
            Waves = waves,
            ClosureOrder = waves.SelectMany(wave => wave.Areas).ToList(),
            ClosedAreas = new HashSet<AreaType>(),
            NextClosureIndex = 0,
            GameStartTime = _utcNow(),
            StartDelaySec = waves.Count > 0 ? waves[0].ClosureAtSeconds : 0,
            IntervalSec = 0
        };

        if (initiallyOpenAreas != null)
        {
            var openAreas = initiallyOpenAreas.ToHashSet();
            state.PhaseDriven = true;
            state.ClosedAreas = mapAreas
                .Where(area => area != AreaType.None && !openAreas.Contains(area))
                .ToHashSet();
        }

        // 동시에 접속한 플레이어가 있어도 하나의 웨이브 시계만 사용한다.
        var actualState = _states.GetOrAdd(matchingId, state);
        if (!ReferenceEquals(actualState, state)) return actualState;

        _logger.LogInformation(
            "Survivor Royale closure schedule initialized: MatchingId={MatchingId}, Waves={Waves}",
            matchingId,
            string.Join(" | ", waves.Select(wave =>
                $"{wave.ClosureAtSeconds}s:{string.Join(',', wave.Areas)}@{wave.ClosedAreaCorruptionPerSecond}/s")));

        return state;
    }

    /// <summary>
    /// 접속·재접속한 클라이언트가 즉시 복원해야 하는 공개 폐쇄 상태다.
    /// 미래 웨이브 대상은 노출하지 않고, 현재 방송 중인 대상 묶음만 제공한다.
    /// </summary>
    public PhaseAreaStateDelta ApplyPhaseSnapshot(long matchingId, SurvivorPhaseSnapshot snapshot)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            state = InitializeMatching(matchingId);

        lock (state.SyncRoot)
        {
            var managedAreas = GameMapData.GetAreas(MapId.School)
                .Select(region => region.AreaType)
                .Where(area => area != AreaType.None)
                .ToHashSet();
            var desiredClosedAreas = managedAreas
                .Where(area => !snapshot.OpenAreas.Contains(area))
                .ToHashSet();
            var newlyClosedAreas = desiredClosedAreas.Except(state.ClosedAreas).OrderBy(area => area).ToArray();
            var reopenedAreas = state.ClosedAreas.Except(desiredClosedAreas).OrderBy(area => area).ToArray();
            bool warningChanged = !state.PhaseWarningAreas.SetEquals(snapshot.WarningAreas);

            state.PhaseDriven = true;
            state.ClosedAreas = desiredClosedAreas;
            state.PhaseWarningAreas = snapshot.WarningAreas.ToHashSet();
            state.PhaseWarningEndsAtUtc = snapshot.WarningAreas.Count > 0
                ? snapshot.PhaseEndsAtUtc
                : DateTime.MinValue;

            return new PhaseAreaStateDelta(
                newlyClosedAreas,
                reopenedAreas,
                warningChanged ? snapshot.WarningAreas.ToArray() : [],
                snapshot.RemainingSeconds,
                snapshot.PhaseEndsAtUtc == DateTime.MaxValue
                    ? 0
                    : new DateTimeOffset(snapshot.PhaseEndsAtUtc).ToUnixTimeMilliseconds());
        }
    }
    public ClosureClientStateSnapshot GetClientStateSnapshot(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return ClosureClientStateSnapshot.Empty;

        lock (state.SyncRoot)
        {
            if (state.PhaseDriven)
            {
                int phaseWarningSeconds = state.PhaseWarningEndsAtUtc == DateTime.MinValue
                    ? 0
                    : Math.Max(0, (int)Math.Ceiling((state.PhaseWarningEndsAtUtc - _utcNow()).TotalSeconds));
                long phaseClosureAtUnixMs = state.PhaseWarningEndsAtUtc == DateTime.MinValue
                    ? 0
                    : new DateTimeOffset(state.PhaseWarningEndsAtUtc).ToUnixTimeMilliseconds();
                return new ClosureClientStateSnapshot(
                    state.ClosedAreas.OrderBy(area => area).ToArray(),
                    state.PhaseWarningAreas.OrderBy(area => area).ToArray(),
                    phaseWarningSeconds,
                    phaseClosureAtUnixMs,
                    0,
                    0);
            }

            double elapsedSeconds = (_utcNow() - state.GameStartTime).TotalSeconds;
            var closedAreas = state.ClosedAreas.OrderBy(area => (int)area).ToArray();

            if (state.NextClosureIndex >= state.Waves.Count)
                return new ClosureClientStateSnapshot(closedAreas, [], 0, 0, 0, 0);

            var nextWave = state.Waves[state.NextClosureIndex];
            double warningAtSeconds = nextWave.ClosureAtSeconds - ClosureWarningSeconds;
            bool isWarningActive = elapsedSeconds >= warningAtSeconds &&
                                   elapsedSeconds < nextWave.ClosureAtSeconds;

            var warningAreas = isWarningActive ? nextWave.Areas.ToArray() : [];
            int warningSeconds = isWarningActive
                ? Math.Max(1, (int)Math.Ceiling(nextWave.ClosureAtSeconds - elapsedSeconds))
                : 0;
            long closureAtUnixMs = isWarningActive
                ? ((DateTimeOffset)state.GameStartTime.AddSeconds(nextWave.ClosureAtSeconds)).ToUnixTimeMilliseconds()
                : 0;

            // 현재 경보가 진행 중이면 그 다음 웨이브, 아니면 아직 시작되지 않은 현재 웨이브의
            // 경보 시각만 공유한다. 대상 지역은 이 패킷에 포함하지 않는다.
            int nextWarningIndex = isWarningActive ? state.NextClosureIndex + 1 : state.NextClosureIndex;
            if (nextWarningIndex >= state.Waves.Count)
                return new ClosureClientStateSnapshot(closedAreas, warningAreas, warningSeconds, closureAtUnixMs, 0, 0);

            double nextWarningAtSeconds = state.Waves[nextWarningIndex].ClosureAtSeconds - ClosureWarningSeconds;
            long nextWarningAtUnixMs = ((DateTimeOffset)state.GameStartTime.AddSeconds(nextWarningAtSeconds))
                .ToUnixTimeMilliseconds();
            int nextWarningSeconds = Math.Max(0, (int)Math.Ceiling(nextWarningAtSeconds - elapsedSeconds));
            return new ClosureClientStateSnapshot(
                closedAreas,
                warningAreas,
                warningSeconds,
                closureAtUnixMs,
                nextWarningSeconds,
                nextWarningAtUnixMs);
        }
    }

    /// <summary>
    /// 현재 시각에 발생한 경고와 폐쇄를 반환한다. 타이머 지연이 있어도 지나간 웨이브를 한 번에 반영한다.
    /// </summary>
    public ClosureScheduleTick CheckClosureSchedule(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return ClosureScheduleTick.Empty;

        lock (state.SyncRoot)
        {
            if (state.PhaseDriven)
                return ClosureScheduleTick.Empty;

            double elapsedSeconds = (_utcNow() - state.GameStartTime).TotalSeconds;
            var closedAreas = new List<AreaType>();

            while (state.NextClosureIndex < state.Waves.Count &&
                   elapsedSeconds >= state.Waves[state.NextClosureIndex].ClosureAtSeconds)
            {
                var wave = state.Waves[state.NextClosureIndex];
                foreach (var area in wave.Areas)
                {
                    if (state.ClosedAreas.Add(area)) closedAreas.Add(area);
                }

                state.NextClosureIndex++;
                _logger.LogInformation(
                    "Survivor Royale closure wave applied: MatchingId={MatchingId}, CloseAt={CloseAt}s, Areas={Areas}, Rate={Rate}/s",
                    matchingId, wave.ClosureAtSeconds, string.Join(',', wave.Areas), wave.ClosedAreaCorruptionPerSecond);
            }

            if (closedAreas.Count > 0)
                return new ClosureScheduleTick([], 0, 0, closedAreas);

            if (state.NextClosureIndex >= state.Waves.Count)
                return ClosureScheduleTick.Empty;

            // 경고는 "다음 하나"가 아니라 경고창에 들어온 모든 웨이브를 함께 낸다 (#229 수리).
            // 순차 폐쇄로 웨이브 간격이 4초가 되면서, 앞 구역이 닫힌 뒤에야 다음 경고가 나가
            // 남은 시간이 3초로 찍혔다 — 묶음의 첫 구역만 15초를 받고 나머지는 사실상 무경고였다.
            // 각 구역은 자기 폐쇄 15초 전에 경고를 받아야 하고, 그 창들은 겹쳐도 된다.
            var warnAreas = new List<AreaType>();
            double earliestClosureAtSeconds = double.MaxValue;
            for (int index = state.NextClosureIndex; index < state.Waves.Count; index++)
            {
                var wave = state.Waves[index];
                if (elapsedSeconds < wave.ClosureAtSeconds - ClosureWarningSeconds)
                    break;
                if (!state.WarningsSent.Add(index))
                    continue;

                warnAreas.AddRange(wave.Areas);
                earliestClosureAtSeconds = Math.Min(earliestClosureAtSeconds, wave.ClosureAtSeconds);
            }

            if (warnAreas.Count == 0)
                return ClosureScheduleTick.Empty;

            // 남은 시간·시각은 이번에 경고한 것들 중 가장 이른 폐쇄 기준이다.
            int remainingSeconds = Math.Max(1, (int)Math.Ceiling(earliestClosureAtSeconds - elapsedSeconds));
            long closureAtUnixMs = ((DateTimeOffset)state.GameStartTime.AddSeconds(earliestClosureAtSeconds))
                .ToUnixTimeMilliseconds();
            _logger.LogInformation(
                "Survivor Royale closure warning: MatchingId={MatchingId}, CloseAt={CloseAt}s, Areas={Areas}, Remaining={Remaining}s",
                matchingId, earliestClosureAtSeconds, string.Join(',', warnAreas), remainingSeconds);
            return new ClosureScheduleTick(warnAreas, remainingSeconds, closureAtUnixMs, []);
        }
    }

    public int GetEnvironmentalCorruptionDelta(long matchingId, AreaType area, int tickSeconds = ResourceTickSeconds)
    {
        if (tickSeconds <= 0 || !_states.TryGetValue(matchingId, out var state)) return 0;

        lock (state.SyncRoot)
        {
            int corruptionPerSecond = GetOvertimeCorruptionPerSecond(state);
            if (area != AreaType.None && state.ClosedAreas.Contains(area))
                corruptionPerSecond += GetCurrentClosedAreaCorruptionPerSecond(state);
            return corruptionPerSecond * tickSeconds;
        }
    }

    public int GetClosedAreaCorruptionPerTick(long matchingId, AreaType area, int tickSeconds = ResourceTickSeconds)
    {
        if (tickSeconds <= 0 || !_states.TryGetValue(matchingId, out var state)) return 0;

        lock (state.SyncRoot)
        {
            return state.ClosedAreas.Contains(area)
                ? GetCurrentClosedAreaCorruptionPerSecond(state) * tickSeconds
                : 0;
        }
    }

    public int GetOvertimeCorruptionPerTick(long matchingId, int tickSeconds = ResourceTickSeconds)
    {
        if (tickSeconds <= 0 || !_states.TryGetValue(matchingId, out var state)) return 0;
        lock (state.SyncRoot)
        {
            return GetOvertimeCorruptionPerSecond(state) * tickSeconds;
        }
    }

    public bool IsOvertimeActive(long matchingId) => GetOvertimeCorruptionPerTick(matchingId, 1) > 0;
    public GlobalClosureTick CheckGlobalClosureSchedule(long matchingId)
    {
        _ = matchingId;
        return GlobalClosureTick.Empty;
    }

    public GlobalClosureClientState GetGlobalClosureClientState(long matchingId)
    {
        _ = matchingId;
        return GlobalClosureClientState.Empty;
    }

    public (int Stage, int CorruptionPerSecond) GetOvertimeStatus(long matchingId)
    {
        int rate = GetOvertimeCorruptionPerTick(matchingId, 1);
        int stage = rate switch
        {
            <= 0 => 0,
            <= 2 => 1,
            <= 4 => 2,
            <= 8 => 3,
            _ => 4
        };
        return (stage, rate);
    }


    /// <summary>전역 오버타임 단계. 마지막 복도 폐쇄 완료 시각(5:20)부터 시작한다.</summary>
    private int GetOvertimeCorruptionPerSecond(MatchingClosureState state)
    {
        if (state.PhaseDriven || state.Waves.Count == 0) return 0;

        double elapsedSeconds = (_utcNow() - state.GameStartTime).TotalSeconds;
        double overtimeStartSeconds = state.Waves[^1].ClosureAtSeconds;
        if (elapsedSeconds < overtimeStartSeconds) return 0;
        if (elapsedSeconds < overtimeStartSeconds + 30) return 2;
        if (elapsedSeconds < overtimeStartSeconds + 50) return 4;
        if (elapsedSeconds < overtimeStartSeconds + 70) return 8;
        return 16;
    }

    private static int GetCurrentClosedAreaCorruptionPerSecond(MatchingClosureState state)
    {
        if (state.PhaseDriven) return 4;

        int lastClosedWaveIndex = state.NextClosureIndex - 1;
        return lastClosedWaveIndex >= 0 && lastClosedWaveIndex < state.Waves.Count
            ? state.Waves[lastClosedWaveIndex].ClosedAreaCorruptionPerSecond
            : 0;
    }

    public MatchingClosureState? GetMatchingState(long matchingId)
    {
        _states.TryGetValue(matchingId, out var state);
        return state;
    }

    public (List<int> closureSequence, List<int> closedAreaIds, int nextAreaType,
        long nextAtUnix, int secondsLeft, bool warningActive, int startDelaySec, int intervalSec)
        GetClosureSnapshot(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return ([], [], -1, -1, -1, false, 0, 0);

        lock (state.SyncRoot)
        {
            var sequence = state.ClosureOrder.Select(area => (int)area).ToList();
            var closed = state.ClosedAreas.Select(area => (int)area).ToList();
            if (state.NextClosureIndex >= state.Waves.Count)
                return (sequence, closed, -1, -1, -1, false, state.StartDelaySec, state.IntervalSec);

            var nextWave = state.Waves[state.NextClosureIndex];
            double elapsedSeconds = (_utcNow() - state.GameStartTime).TotalSeconds;
            double remainingSeconds = nextWave.ClosureAtSeconds - elapsedSeconds;
            long nextAtUnix = ((DateTimeOffset)state.GameStartTime.AddSeconds(nextWave.ClosureAtSeconds)).ToUnixTimeSeconds();
            return (
                sequence,
                closed,
                (int)nextWave.Areas[0],
                nextAtUnix,
                remainingSeconds > 0 ? (int)Math.Ceiling(remainingSeconds) : 0,
                remainingSeconds > 0 && remainingSeconds <= ClosureWarningSeconds,
                state.StartDelaySec,
                state.IntervalSec);
        }
    }

    public bool IsAreaClosed(long matchingId, AreaType area)
    {
        if (!_states.TryGetValue(matchingId, out var state)) return false;
        lock (state.SyncRoot) return state.ClosedAreas.Contains(area);
    }

    public void CleanupMatching(long matchingId)
    {
        _states.TryRemove(matchingId, out _);
    }
}

public sealed record ClosureWaveDefinition(
    int ClosureAtSeconds,
    IReadOnlyList<AreaType> Areas,
    int ClosedAreaCorruptionPerSecond);

public sealed record ClosureScheduleTick(
    IReadOnlyList<AreaType> WarningAreas,
    int WarningSeconds,
    long ClosureAtUnixMs,
    IReadOnlyList<AreaType> ClosedAreas)
{
    public static readonly ClosureScheduleTick Empty = new([], 0, 0, []);
}

public sealed record PhaseAreaStateDelta(
    IReadOnlyList<AreaType> ClosedAreas,
    IReadOnlyList<AreaType> ReopenedAreas,
    IReadOnlyList<AreaType> WarningAreas,
    int WarningSeconds,
    long TransitionAtUnixMs);
public sealed record ClosureClientStateSnapshot(
    IReadOnlyList<AreaType> ClosedAreas,
    IReadOnlyList<AreaType> WarningAreas,
    int WarningSeconds,
    long ClosureAtUnixMs,
    int NextWarningSeconds,
    long NextWarningAtUnixMs)
{
    public static readonly ClosureClientStateSnapshot Empty = new([], [], 0, 0, 0, 0);
}

public sealed record GlobalClosureTick(bool IsActive, int SecondsRemaining, long ClosureAtUnixMs)
{
    public static readonly GlobalClosureTick Empty = new(false, -1, 0);
    public bool HasTransition => SecondsRemaining >= 0;
}

public sealed record GlobalClosureClientState(
    bool IsKnown,
    bool IsActive,
    int SecondsRemaining,
    long ClosureAtUnixMs)
{
    public static readonly GlobalClosureClientState Empty = new(false, false, 0, 0);
}

public class MatchingClosureState
{
    internal object SyncRoot { get; } = new();
    public long MatchingId { get; set; }
    public List<ClosureWaveDefinition> Waves { get; set; } = new();
    public List<AreaType> ClosureOrder { get; set; } = new();
    public HashSet<AreaType> ClosedAreas { get; set; } = new();
    public int NextClosureIndex { get; set; }
    public DateTime GameStartTime { get; set; }
    public HashSet<int> WarningsSent { get; set; } = new();
    public bool GlobalClosureWarningSent { get; set; }
    public bool GlobalClosureActiveSent { get; set; }
    public int StartDelaySec { get; set; }
    public int IntervalSec { get; set; }
    public bool PhaseDriven { get; set; }
    public HashSet<AreaType> PhaseWarningAreas { get; set; } = [];
    public DateTime PhaseWarningEndsAtUtc { get; set; }
}
