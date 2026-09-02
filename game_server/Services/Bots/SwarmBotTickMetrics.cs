using System.Collections.Immutable;

namespace game_server.services;

/// <summary>
///     봇 이동 틱 계측 창 — 매치 하나가 소유한다(<see cref="SwarmMatchRuntime"/>). 샘플 기록은 그 매치의
///     잠금 안에서만 돌고, 잠금이 바빠 버린 펄스는 잠금 밖에서 Interlocked로만 누적한다. 200틱마다
///     불변 배치를 내보내고 창을 비운다.
/// </summary>
internal sealed class SwarmBotTickMetrics
{
    public const int WindowSize = 200;

    private readonly List<double> _tickSamples = new(WindowSize);
    private readonly List<double> _snapshotSamples = new(WindowSize);
    private readonly List<double> _planningSamples = new(WindowSize);
    private readonly List<double> _walkingSamples = new(WindowSize);
    private readonly List<double> _broadcastSamples = new(WindowSize);
    private double _totalElapsedMilliseconds;
    private double _maxElapsedMilliseconds;
    private int _busySkips;
    private int _consecutiveBusySkips;
    private int _maxConsecutiveBusySkips;

    public int BusySkips => Volatile.Read(ref _busySkips);
    public int ConsecutiveBusySkips => Volatile.Read(ref _consecutiveBusySkips);
    public int SampleCount => _tickSamples.Count;

    /// <summary>잠금이 바빠 펄스를 버렸다 — 잠금 밖에서 호출되므로 Interlocked만 쓴다.</summary>
    public void RecordBusySkip()
    {
        Interlocked.Increment(ref _busySkips);
        int consecutive = Interlocked.Increment(ref _consecutiveBusySkips);
        int observedMax = Volatile.Read(ref _maxConsecutiveBusySkips);
        while (consecutive > observedMax)
        {
            int previous = Interlocked.CompareExchange(ref _maxConsecutiveBusySkips, consecutive, observedMax);
            if (previous == observedMax)
                break;
            observedMax = previous;
        }
    }

    /// <summary>완료한 틱을 기록한다 (매치 잠금 안). 창이 차면 배치를 돌려주고 창을 비운다.</summary>
    public SwarmBotTickMetricsBatch? Record(long matchingId, SwarmBotTickSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Interlocked.Exchange(ref _consecutiveBusySkips, 0);
        _tickSamples.Add(sample.TotalElapsedMilliseconds);
        _snapshotSamples.Add(sample.SnapshotElapsedMilliseconds);
        _planningSamples.Add(sample.PlanningElapsedMilliseconds);
        _walkingSamples.Add(sample.WalkingElapsedMilliseconds);
        _broadcastSamples.Add(sample.BroadcastElapsedMilliseconds);
        _totalElapsedMilliseconds += sample.TotalElapsedMilliseconds;
        _maxElapsedMilliseconds = Math.Max(_maxElapsedMilliseconds, sample.TotalElapsedMilliseconds);
        if (_tickSamples.Count < WindowSize)
            return null;

        var batch = new SwarmBotTickMetricsBatch(
            matchingId,
            _tickSamples.ToImmutableArray(),
            _snapshotSamples.ToImmutableArray(),
            _planningSamples.ToImmutableArray(),
            _walkingSamples.ToImmutableArray(),
            _broadcastSamples.ToImmutableArray(),
            _totalElapsedMilliseconds,
            _maxElapsedMilliseconds,
            Interlocked.Exchange(ref _busySkips, 0),
            Interlocked.Exchange(ref _maxConsecutiveBusySkips, 0));
        _tickSamples.Clear();
        _snapshotSamples.Clear();
        _planningSamples.Clear();
        _walkingSamples.Clear();
        _broadcastSamples.Clear();
        _totalElapsedMilliseconds = 0d;
        _maxElapsedMilliseconds = 0d;
        return batch;
    }
}

internal sealed record SwarmBotTickSample(
    double TotalElapsedMilliseconds,
    double SnapshotElapsedMilliseconds,
    double PlanningElapsedMilliseconds,
    double WalkingElapsedMilliseconds,
    double BroadcastElapsedMilliseconds);

internal sealed record SwarmBotTickMetricsBatch(
    long MatchingId,
    ImmutableArray<double> TickSamples,
    ImmutableArray<double> SnapshotSamples,
    ImmutableArray<double> PlanningSamples,
    ImmutableArray<double> WalkingSamples,
    ImmutableArray<double> BroadcastSamples,
    double TotalElapsedMilliseconds,
    double MaxElapsedMilliseconds,
    int BusySkips,
    int MaxConsecutiveBusySkips);
