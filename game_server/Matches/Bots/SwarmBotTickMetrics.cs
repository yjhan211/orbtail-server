using System.Collections.Immutable;

namespace game_server.matches.bots;

/// <summary>
///     봇 이동 틱 계측 창 — 매치 하나가 소유한다(<see cref="game_server.matches.MatchRuntime"/>). 샘플 기록은 그 매치의
///     잠금 안에서만 수행한다. 200틱마다
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
    public int SampleCount => _tickSamples.Count;

    /// <summary>완료한 틱을 기록한다 (매치 잠금 안). 창이 차면 배치를 돌려주고 창을 비운다.</summary>
    public SwarmBotTickMetricsBatch? Record(long matchingId, SwarmBotTickSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
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
            _maxElapsedMilliseconds);
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
    double MaxElapsedMilliseconds);
