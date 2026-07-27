using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
/// Survivor Royale P0의 서버 권위 구역 폐쇄·오버타임 상태를 관리한다.
/// 복도는 하나의 연결 영역이므로 절대로 폐쇄하지 않는다.
/// </summary>
public class AreaClosureManager
{
    public const int ClosureWarningSeconds = 15;
    public const int ResourceTickSeconds = 5;

    private static readonly IReadOnlyList<ClosureWaveDefinition> DefaultP0Waves =
    [
        new(105, [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2], 4),
        new(165, [AreaType.Classroom4, AreaType.Classroom3], 6),
        new(215, [AreaType.Library, AreaType.Gym], 8),
        new(255, [AreaType.Storage, AreaType.Junkyard, AreaType.AdminOffice], 10),
        new(290, [AreaType.StaffRoom, AreaType.Junkyard2, AreaType.Storage2], 12)
    ];

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
    public MatchingClosureState InitializeMatching(long matchingId, List<JobTitle>? jobsInMatching = null)
    {
        _ = jobsInMatching;

        // 동일 매치의 뒤늦은 접속(재접속 포함)이 폐쇄 시계와 누적 폐쇄 상태를
        // 처음부터 다시 만들면 안 된다. 최초 접속만 상태를 생성한다.
        if (_states.TryGetValue(matchingId, out var existingState))
            return existingState;

        var mapAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .ToHashSet();
        var waves = DefaultP0Waves
            .Select(wave => wave with
            {
                Areas = wave.Areas.Where(mapAreas.Contains).ToArray()
            })
            .Where(wave => wave.Areas.Count > 0)
            .ToList();

        if (waves.Any(wave => wave.Areas.Any(area => area.IsCorridor())))
            throw new InvalidOperationException("Survivor Royale P0 closure schedule must not contain corridors.");

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
    public ClosureClientStateSnapshot GetClientStateSnapshot(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return ClosureClientStateSnapshot.Empty;

        lock (state.SyncRoot)
        {
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

            var nextWave = state.Waves[state.NextClosureIndex];
            double warningAtSeconds = nextWave.ClosureAtSeconds - ClosureWarningSeconds;
            if (elapsedSeconds < warningAtSeconds || !state.WarningsSent.Add(state.NextClosureIndex))
                return ClosureScheduleTick.Empty;

            int remainingSeconds = Math.Max(1, (int)Math.Ceiling(nextWave.ClosureAtSeconds - elapsedSeconds));
            long closureAtUnixMs = ((DateTimeOffset)state.GameStartTime.AddSeconds(nextWave.ClosureAtSeconds))
                .ToUnixTimeMilliseconds();
            _logger.LogInformation(
                "Survivor Royale closure warning: MatchingId={MatchingId}, CloseAt={CloseAt}s, Areas={Areas}, Remaining={Remaining}s",
                matchingId, nextWave.ClosureAtSeconds, string.Join(',', nextWave.Areas), remainingSeconds);
            return new ClosureScheduleTick(nextWave.Areas, remainingSeconds, closureAtUnixMs, []);
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


    /// <summary>운동장 전역 오버타임 단계. 마지막 폐쇄 완료 시각(4:50)부터 시작한다.</summary>
    private int GetOvertimeCorruptionPerSecond(MatchingClosureState state)
    {
        if (state.Waves.Count == 0) return 0;

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
    public int StartDelaySec { get; set; }
    public int IntervalSec { get; set; }
}
