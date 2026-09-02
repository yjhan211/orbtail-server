using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace game_server.services;

/// <summary>
///     Owns a preregistered, fail-closed timer gate and metrics window for each match. Timer
///     pulses that arrive while a match is already walking are intentionally dropped; they never
///     create a deferred catch-up tick.
/// </summary>
internal sealed class SwarmBotTickCoordinator
{
    private const int MetricsWindowSize = 200;
    private readonly ConcurrentDictionary<long, TickState> _states = new();

    public bool RegisterMatching(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        return _states.TryAdd(matchingId, new TickState());
    }

    /// <summary>
    ///     Claims the preregistered match tick. Missing or cleared state rejects work; a busy
    ///     state records the dropped pulse and does not queue a catch-up execution.
    /// </summary>
    public bool TryBegin(
        long matchingId,
        bool trackBusySkip,
        out SwarmBotTickLease? lease)
    {
        lease = null;
        if (!_states.TryGetValue(matchingId, out TickState? state))
            return false;

        lock (state.Gate)
        {
            if (state.Cleared)
                return false;
            if (state.Running)
            {
                if (trackBusySkip)
                    RecordBusySkip(state);

                return false;
            }

            state.Running = true;
            lease = new SwarmBotTickLease(this, matchingId, state);
            return true;
        }
    }

    /// <summary>
    ///     Records a dropped pulse when the runtime monitor itself is contended. This is a
    ///     lookup-only path and never creates or revives match state.
    /// </summary>
    public bool TryRecordBusySkip(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out TickState? state))
            return false;

        lock (state.Gate)
        {
            if (state.Cleared)
                return false;

            RecordBusySkip(state);
            return true;
        }
    }

    /// <summary>
    ///     Records a completed attempt while its outer runtime lease is still held. A returned
    ///     batch is immutable and may be published only after the caller releases that lease.
    /// </summary>
    public SwarmBotTickMetricsBatch? Record(
        SwarmBotTickLease lease,
        SwarmBotTickSample sample)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sample);
        if (!ReferenceEquals(lease.Owner, this))
        {
            throw new InvalidOperationException(
                "The bot tick lease belongs to another coordinator.");
        }

        TickState state = lease.State;
        lock (state.Gate)
        {
            if (lease.IsRetired || state.Cleared || !state.Running)
                return null;

            state.ConsecutiveBusySkips = 0;
            state.TickSamples.Add(sample.TotalElapsedMilliseconds);
            state.SnapshotSamples.Add(sample.SnapshotElapsedMilliseconds);
            state.PlanningSamples.Add(sample.PlanningElapsedMilliseconds);
            state.WalkingSamples.Add(sample.WalkingElapsedMilliseconds);
            state.BroadcastSamples.Add(sample.BroadcastElapsedMilliseconds);
            state.TotalElapsedMilliseconds += sample.TotalElapsedMilliseconds;
            state.MaxElapsedMilliseconds = Math.Max(
                state.MaxElapsedMilliseconds,
                sample.TotalElapsedMilliseconds);
            if (state.TickSamples.Count < MetricsWindowSize)
                return null;

            var batch = new SwarmBotTickMetricsBatch(
                lease.MatchingId,
                state.TickSamples.ToImmutableArray(),
                state.SnapshotSamples.ToImmutableArray(),
                state.PlanningSamples.ToImmutableArray(),
                state.WalkingSamples.ToImmutableArray(),
                state.BroadcastSamples.ToImmutableArray(),
                state.TotalElapsedMilliseconds,
                state.MaxElapsedMilliseconds,
                state.BusySkips,
                state.MaxConsecutiveBusySkips);
            state.TickSamples.Clear();
            state.SnapshotSamples.Clear();
            state.PlanningSamples.Clear();
            state.WalkingSamples.Clear();
            state.BroadcastSamples.Clear();
            state.TotalElapsedMilliseconds = 0d;
            state.MaxElapsedMilliseconds = 0d;
            state.BusySkips = 0;
            state.MaxConsecutiveBusySkips = 0;
            return batch;
        }
    }

    /// <summary>
    ///     Retires an owner only after its outer runtime lease has been disposed. This never
    ///     recreates cleared state, so a late detached worker cannot revive a finalized match.
    /// </summary>
    public void Retire(SwarmBotTickLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(lease.Owner, this))
        {
            throw new InvalidOperationException(
                "The bot tick lease belongs to another coordinator.");
        }

        TickState state = lease.State;
        lock (state.Gate)
        {
            if (lease.IsRetired)
                return;

            lease.IsRetired = true;
            state.Running = false;
            if (!state.Cleared)
                state.ConsecutiveBusySkips = 0;
        }
    }

    /// <summary>
    ///     Terminal cleanup marks the captured state closed and removes only that exact instance.
    /// </summary>
    public void ClearMatching(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out TickState? state))
            return;

        lock (state.Gate)
        {
            state.Cleared = true;
            state.Running = false;
            state.TickSamples.Clear();
            state.SnapshotSamples.Clear();
            state.PlanningSamples.Clear();
            state.WalkingSamples.Clear();
            state.BroadcastSamples.Clear();
        }

        ((ICollection<KeyValuePair<long, TickState>>)_states)
            .Remove(new KeyValuePair<long, TickState>(matchingId, state));
    }

    internal SwarmBotTickDiagnostics GetDiagnostics(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out TickState? state))
            return new SwarmBotTickDiagnostics(false, false, false, 0, 0);

        lock (state.Gate)
        {
            return new SwarmBotTickDiagnostics(
                true,
                state.Running,
                state.Cleared,
                state.BusySkips,
                state.ConsecutiveBusySkips);
        }
    }

    private static void RecordBusySkip(TickState state)
    {
        state.BusySkips++;
        state.ConsecutiveBusySkips++;
        state.MaxConsecutiveBusySkips = Math.Max(
            state.MaxConsecutiveBusySkips,
            state.ConsecutiveBusySkips);
    }

    internal sealed class TickState
    {
        public object Gate { get; } = new();
        public bool Running { get; set; }
        public bool Cleared { get; set; }
        public int BusySkips { get; set; }
        public int ConsecutiveBusySkips { get; set; }
        public int MaxConsecutiveBusySkips { get; set; }
        public double TotalElapsedMilliseconds { get; set; }
        public double MaxElapsedMilliseconds { get; set; }
        public List<double> TickSamples { get; } = new(MetricsWindowSize);
        public List<double> SnapshotSamples { get; } = new(MetricsWindowSize);
        public List<double> PlanningSamples { get; } = new(MetricsWindowSize);
        public List<double> WalkingSamples { get; } = new(MetricsWindowSize);
        public List<double> BroadcastSamples { get; } = new(MetricsWindowSize);
    }

    internal sealed class SwarmBotTickLease
    {
        internal SwarmBotTickLease(
            SwarmBotTickCoordinator owner,
            long matchingId,
            TickState state)
        {
            Owner = owner;
            MatchingId = matchingId;
            State = state;
        }

        internal SwarmBotTickCoordinator Owner { get; }
        internal TickState State { get; }
        internal bool IsRetired { get; set; }
        public long MatchingId { get; }
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

internal readonly record struct SwarmBotTickDiagnostics(
    bool IsRegistered,
    bool IsRunning,
    bool IsCleared,
    int BusySkips,
    int ConsecutiveBusySkips);
