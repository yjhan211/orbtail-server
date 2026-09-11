using network.common;
using network.common.data;

namespace game_server.matches;

/// <summary>
///     매치 하나의 폐쇄 시간표와 진행 상태. MatchRuntime이 소유하며 읽기·변경 모두 매치 잠금 안에서만 일어나므로
///     자체 잠금은 없다. 시간표는 자기장 수축 곡선에서 파생하고(MatchFieldService.SwarmFieldDerivedWaves) 폐쇄 틱은
///     MatchFieldService가 돌린다.
/// </summary>
public sealed class AreaClosureState(Func<DateTime>? utcNow = null)
{
    public const int ClosureWarningSeconds = 15;

    private MatchingClosureState? _state;
    private bool _released;
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>시간표를 한 번 만든다. 재접속을 포함한 뒤늦은 호출은 기존 시계를 그대로 돌려준다.</summary>
    public MatchingClosureState InitializeMatching(IReadOnlyList<ClosureWaveDefinition>? wavesOverride = null)
    {
        if (_released)
            throw new InvalidOperationException("Match is not available.");
        if (_state is { } existingState)
            return existingState;

        var mapAreas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .ToHashSet();
        // wavesOverride 미지정(자기장 비활성)은 폐쇄 없음.
        var waves = (wavesOverride ?? [])
            .Select(wave => wave with
            {
                Areas = wave.Areas.Where(mapAreas.Contains).ToArray()
            })
            .Where(wave => wave.Areas.Count > 0)
            .ToList();

        _state = new MatchingClosureState
        {
            Waves = waves,
            GameStartTime = _utcNow()
        };
        return _state;
    }

    internal void Release()
    {
        _released = true;
        _state = null;
    }

    /// <summary>클라이언트·봇이 읽는 현재 폐쇄 상태. 경고는 15초 창 안의 모든 웨이브를 함께 담는다.</summary>
    public ClosureClientStateSnapshot GetClientStateSnapshot()
    {
        if (_state is not { } state)
            return ClosureClientStateSnapshot.Empty;

        double elapsedSeconds = (_utcNow() - state.GameStartTime).TotalSeconds;
        var closedAreas = state.ClosedAreas.OrderBy(area => (int)area).ToArray();

        if (state.NextClosureIndex >= state.Waves.Count)
            return new ClosureClientStateSnapshot(closedAreas, [], 0, 0, 0, 0);

        // 순차 폐쇄로 웨이브가 4초 간격이 되면 다음 한 건만 봐서는 뒤 구역이 4초짜리 경고만 받는다.
        // 봇 대피와 클라 경고가 같이 이 스냅샷을 읽으므로 창 안의 웨이브를 전부 담는다.
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

        // 현재 경보가 진행 중이면 그 다음 웨이브, 아니면 아직 시작되지 않은 현재 웨이브의 경보 시각만 공유한다.
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

    /// <summary>현재 시각에 발생한 경고와 폐쇄를 돌려준다. 타이머가 늦어도 지나간 웨이브를 한 번에 반영한다.</summary>
    public ClosureScheduleTick CheckClosureSchedule()
    {
        if (_state is not { } state)
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
        }

        if (closedAreas.Count > 0)
            return new ClosureScheduleTick([], 0, 0, closedAreas);

        if (state.NextClosureIndex >= state.Waves.Count)
            return ClosureScheduleTick.Empty;

        // 각 구역은 자기 폐쇄 15초 전에 경고를 받아야 하고, 그 창들은 겹쳐도 된다.
        // 경고는 "다음 하나"가 아니라 경고창에 들어온 모든 웨이브를 함께 낸다.
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
        return new ClosureScheduleTick(warnAreas, remainingSeconds, closureAtUnixMs, []);
    }

    public MatchingClosureState? GetMatchingState() => _state;

    /// <summary>이 매치의 현재 안전 반경. 시간표가 아직 없으면 전 맵이 안전하다.</summary>
    public double GetSafeDistance(DateTime nowUtc)
    {
        if (_state == null)
            return double.MaxValue;
        return SwarmPressureField.GetSafeDistanceAtElapsed((nowUtc - _state.GameStartTime).TotalSeconds);
    }

    public bool IsAreaClosed(AreaType area) => _state?.ClosedAreas.Contains(area) == true;
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

/// <summary>폐쇄 시계 하나. 웨이브 목록과 닫힌 구역, 다음 웨이브 순번, 이미 경고한 웨이브 순번을 든다.</summary>
public class MatchingClosureState
{
    public List<ClosureWaveDefinition> Waves { get; set; } = new();
    public HashSet<AreaType> ClosedAreas { get; set; } = new();
    public int NextClosureIndex { get; set; }
    public DateTime GameStartTime { get; set; }
    public HashSet<int> WarningsSent { get; set; } = new();
}
