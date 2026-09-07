using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>매치 런타임이 소유한 폐쇄 시간표를 초기화하고 진행·조회한다.</summary>
public class AreaClosureManager
{
    public const int ClosureWarningSeconds = 15;
    public const int ResourceTickSeconds = MatchRuntime.EnvironmentalTickIntervalSeconds;

    /// <summary>
    ///     #272 자기장 파생 웨이브: 구역별 완전-밖 시각(안전 반경이 구역 최근접 셀 거리
    ///     아래로 내려가는 순간)을 폐쇄 시각으로 삼는다 — 기존 웨이브 배선(경고 15초·문 잠금·
    ///     꼬리 파괴·봇 대피·재접속 스냅샷)이 그대로 소비한다. 폐쇄 구역 틱 피해은 0 —
    ///     압박은 자기장 경사(경계 초과 거리 비례)가 전담한다.
    ///     운동장(중심 거리 0)은 수축 완료 시각에 닫힌다 — 최종 폐쇄 = 타이머 만료 = 오버타임 개시.
    /// </summary>
    public static List<ClosureWaveDefinition> BuildSwarmFieldWaves(double holdSeconds, double shrinkSeconds)
    {
        // 폐쇄 시각 = 수축 곡선의 역함수 (#272 ease-in) — 경계 판정·렌더와 같은 곡선.
        return SwarmPressureField.GetKnownAreas()
            .Select(area => (Area: area, MinDistance: SwarmPressureField.GetAreaMinDistance(area)))
            .Select(pair => new ClosureWaveDefinition(
                (int)Math.Ceiling(holdSeconds + shrinkSeconds *
                                  SwarmPressureField.GetProgressAtSafeDistance(pair.MinDistance)),
                [pair.Area],
                0))
            .OrderBy(wave => wave.ClosureAtSeconds)
            .ToList();
    }

    private readonly long _matchingId;
    private MatchingClosureState? _state;
    private readonly object _initializationLock = new();
    private bool _released;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;

    internal AreaClosureManager(long matchingId, ILogger logger, Func<DateTime>? utcNow = null)
    {
        _matchingId = matchingId;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 매치별 고정 P0 웨이브를 만든다.
    /// </summary>
    public MatchingClosureState InitializeMatching(
        IEnumerable<AreaType>? initiallyOpenAreas = null,
        IReadOnlyList<ClosureWaveDefinition>? wavesOverride = null)
    {
        // 동일 매치의 뒤늦은 접속(재접속 포함)이 폐쇄 시계와 누적 폐쇄 상태를
        // 처음부터 다시 만들면 안 된다. 최초 접속만 상태를 생성한다.
        lock (_initializationLock)
        {
            if (_released)
                throw new InvalidOperationException($"Match is not available: {_matchingId}");
            if (Volatile.Read(ref _state) is { } existingState)
                return existingState;

            var mapAreas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
                .Select(region => region.AreaType)
                .ToHashSet();
            // wavesOverride 미지정(자기장 비활성)은 폐쇄 없음 — 레거시 School 폴백 시절에도
            // School2 구역과 교집합이 없어 빈 웨이브였다 (#310).
            var waves = (wavesOverride ?? [])
                .Select(wave => wave with
                {
                    Areas = wave.Areas.Where(mapAreas.Contains).ToArray()
                })
                .Where(wave => wave.Areas.Count > 0)
                .ToList();

            var state = new MatchingClosureState
            {
                MatchingId = _matchingId,
                Waves = waves,
                ClosureOrder = waves.SelectMany(wave => wave.Areas).ToList(),
                ClosedAreas = new HashSet<AreaType>(),
                NextClosureIndex = 0,
                GameStartTime = _utcNow()
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
            Volatile.Write(ref _state, state);

            _logger.LogInformation(
                "Swarm closure schedule initialized: MatchingId={MatchingId}, Waves={Waves}",
                _matchingId,
                string.Join(" | ", waves.Select(wave =>
                    $"{wave.ClosureAtSeconds}s:{string.Join(',', wave.Areas)}@{wave.ClosedAreaDamagePerSecond}/s")));

            return state;
        }
    }

    internal void Release()
    {
        lock (_initializationLock)
        {
            _released = true;
            Interlocked.Exchange(ref _state, null);
        }
    }

    public ClosureClientStateSnapshot GetClientStateSnapshot()
    {
        if (Volatile.Read(ref _state) is not { } state)
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

            // 경고는 15초 창 안의 모든 웨이브를 담는다 (#229). 순차 폐쇄로 웨이브가 4초 간격이
            // 되면서, 다음 한 건만 보면 스태거 그룹의 두 번째 이후 구역은 4초짜리 경고만 받는다.
            // 봇 대피와 클라 경고가 같이 이 스냅샷을 읽으므로 여기서 한 번에 고친다.
            var warningWaveList = new List<AreaType>();
            double earliestWarnedClosureAtSeconds = double.MaxValue;
            for (int index = state.NextClosureIndex; index < state.Waves.Count; index++)
            {
                var wave = state.Waves[index];
                if (elapsedSeconds < wave.ClosureAtSeconds - ClosureWarningSeconds) break;
                if (elapsedSeconds >= wave.ClosureAtSeconds) continue;

                warningWaveList.AddRange(wave.Areas);
                earliestWarnedClosureAtSeconds =
                    Math.Min(earliestWarnedClosureAtSeconds, wave.ClosureAtSeconds);
            }

            bool isWarningActive = warningWaveList.Count > 0;
            var warningAreas = warningWaveList.Distinct().ToArray();
            int warningSeconds = isWarningActive
                ? Math.Max(1, (int)Math.Ceiling(earliestWarnedClosureAtSeconds - elapsedSeconds))
                : 0;
            long closureAtUnixMs = isWarningActive
                ? ((DateTimeOffset)state.GameStartTime.AddSeconds(earliestWarnedClosureAtSeconds))
                    .ToUnixTimeMilliseconds()
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
    public ClosureScheduleTick CheckClosureSchedule()
    {
        if (Volatile.Read(ref _state) is not { } state) return ClosureScheduleTick.Empty;

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
                    "Swarm closure wave applied: MatchingId={MatchingId}, CloseAt={CloseAt}s, Areas={Areas}, Rate={Rate}/s",
                    _matchingId, wave.ClosureAtSeconds, string.Join(',', wave.Areas), wave.ClosedAreaDamagePerSecond);
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
                "Swarm closure warning: MatchingId={MatchingId}, CloseAt={CloseAt}s, Areas={Areas}, Remaining={Remaining}s",
                _matchingId, earliestClosureAtSeconds, string.Join(',', warnAreas), remainingSeconds);
            return new ClosureScheduleTick(warnAreas, remainingSeconds, closureAtUnixMs, []);
        }
    }

    public int GetClosedAreaDamagePerTick(AreaType area, int tickSeconds = ResourceTickSeconds)
    {
        if (tickSeconds <= 0 || Volatile.Read(ref _state) is not { } state) return 0;

        lock (state.SyncRoot)
        {
            return state.ClosedAreas.Contains(area)
                ? GetCurrentClosedAreaDamagePerSecond(state) * tickSeconds
                : 0;
        }
    }

    public int GetOvertimeDamagePerTick(int tickSeconds = ResourceTickSeconds)
    {
        if (tickSeconds <= 0 || Volatile.Read(ref _state) is not { } state) return 0;
        lock (state.SyncRoot)
        {
            return GetOvertimeDamagePerSecond(state) * tickSeconds;
        }
    }

    public GlobalClosureClientState GetGlobalClosureClientState()
    {
        _ = _matchingId;
        return GlobalClosureClientState.Empty;
    }


    /// <summary>전역 오버타임 단계. 마지막 복도 폐쇄 완료 시각(5:20)부터 시작한다.</summary>
    private int GetOvertimeDamagePerSecond(MatchingClosureState state)
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

    private static int GetCurrentClosedAreaDamagePerSecond(MatchingClosureState state)
    {
        if (state.PhaseDriven) return 4;

        int lastClosedWaveIndex = state.NextClosureIndex - 1;
        return lastClosedWaveIndex >= 0 && lastClosedWaveIndex < state.Waves.Count
            ? state.Waves[lastClosedWaveIndex].ClosedAreaDamagePerSecond
            : 0;
    }

    public MatchingClosureState? GetMatchingState()
    {
        return Volatile.Read(ref _state);
    }

    public (List<int> closureSequence, List<int> closedAreaIds, int nextAreaType,
        long nextAtUnix, int secondsLeft, bool warningActive)
        GetClosureSnapshot()
    {
        if (Volatile.Read(ref _state) is not { } state)
            return ([], [], -1, -1, -1, false);

        lock (state.SyncRoot)
        {
            var sequence = state.ClosureOrder.Select(area => (int)area).ToList();
            var closed = state.ClosedAreas.Select(area => (int)area).ToList();
            if (state.NextClosureIndex >= state.Waves.Count)
                return (sequence, closed, -1, -1, -1, false);

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
                remainingSeconds > 0 && remainingSeconds <= ClosureWarningSeconds);
        }
    }

    public bool IsAreaClosed(AreaType area)
    {
        if (Volatile.Read(ref _state) is not { } state) return false;
        lock (state.SyncRoot) return state.ClosedAreas.Contains(area);
    }

}

public sealed record ClosureWaveDefinition(
    int ClosureAtSeconds,
    IReadOnlyList<AreaType> Areas,
    int ClosedAreaDamagePerSecond);

public sealed record ClosureScheduleTick(
    IReadOnlyList<AreaType> WarningAreas,
    int WarningSeconds,
    long ClosureAtUnixMs,
    IReadOnlyList<AreaType> ClosedAreas)
{
    public static readonly ClosureScheduleTick Empty = new([], 0, 0, []);
}

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
    public bool PhaseDriven { get; set; }
    public HashSet<AreaType> PhaseWarningAreas { get; set; } = [];
    public DateTime PhaseWarningEndsAtUtc { get; set; }
}
