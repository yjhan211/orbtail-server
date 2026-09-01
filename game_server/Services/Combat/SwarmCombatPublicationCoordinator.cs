using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using network.interfaces;
using network.packets;

namespace game_server.services;

/// <summary>
///     Owns one prepare-to-dispatch publication turn per match. Required and ordered callers wait
///     in separate FIFO lanes outside the match runtime monitor. Required work has priority. Once a
///     due realtime caller is observed behind an active turn, it receives one bounded handoff
///     opportunity ahead of ordered work; ordered work resumes if that reservation expires or its
///     clock cannot be read. Realtime and required acquisition rebase the monotonic start-based due,
///     while ordered acquisition preserves it. Captured packet bytes and deferred steps are replayed
///     in their original call order.
/// </summary>
internal sealed class SwarmCombatPublicationCoordinator
{
    private readonly Func<ulong> _getTimestamp;
    private readonly ulong _realtimeIntervalTicks;
    private readonly ulong _realtimeHandoffGraceTicks;
    private readonly ulong _realtimeHandoffGraceStopwatchTicks;
    private readonly ConcurrentDictionary<long, MatchTurnState> _matchStates = new();
    private readonly AsyncLocal<CaptureFrame?> _activeCapture = new();
    private readonly AsyncLocal<int> _dispatchDepth = new();
    private long _nextTurnId;
    private long _nextBoundaryId;

    public SwarmCombatPublicationCoordinator(
        TimeSpan realtimeInterval,
        Func<ulong>? getTimestamp = null,
        ulong? timestampFrequency = null,
        TimeSpan? realtimeHandoffGrace = null)
    {
        if (realtimeInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(realtimeInterval));

        ulong frequency = timestampFrequency ?? checked((ulong)Stopwatch.Frequency);
        if (frequency == 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

        UInt128 numerator =
            (UInt128)checked((ulong)realtimeInterval.Ticks) * frequency;
        UInt128 intervalTicks =
            (numerator + (ulong)TimeSpan.TicksPerSecond - 1) /
            (ulong)TimeSpan.TicksPerSecond;
        if (intervalTicks == 0 || intervalTicks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeInterval),
                "The combat publication interval must fit within half the monotonic timestamp range.");
        }

        TimeSpan handoffGrace;
        try
        {
            handoffGrace = realtimeHandoffGrace ??
                           TimeSpan.FromTicks(checked(realtimeInterval.Ticks * 2));
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeHandoffGrace),
                "The realtime handoff grace is too large.");
        }

        if (handoffGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(realtimeHandoffGrace));

        UInt128 handoffNumerator =
            (UInt128)checked((ulong)handoffGrace.Ticks) * frequency;
        UInt128 handoffTicks =
            (handoffNumerator + (ulong)TimeSpan.TicksPerSecond - 1) /
            (ulong)TimeSpan.TicksPerSecond;
        if (handoffTicks == 0 || handoffTicks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeHandoffGrace),
                "The realtime handoff grace must fit within half the monotonic timestamp range.");
        }

        UInt128 stopwatchNumerator =
            (UInt128)checked((ulong)handoffGrace.Ticks) *
            checked((ulong)Stopwatch.Frequency);
        UInt128 stopwatchTicks =
            (stopwatchNumerator + (ulong)TimeSpan.TicksPerSecond - 1) /
            (ulong)TimeSpan.TicksPerSecond;
        if (stopwatchTicks == 0 || stopwatchTicks > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(realtimeHandoffGrace));

        _realtimeIntervalTicks = (ulong)intervalTicks;
        _realtimeHandoffGraceTicks = (ulong)handoffTicks;
        _realtimeHandoffGraceStopwatchTicks = (ulong)stopwatchTicks;
        _getTimestamp = getTimestamp ?? (static () => unchecked((ulong)Stopwatch.GetTimestamp()));
    }

    /// <summary>
    ///     Registers the coordinator entry at match start. Publication begins fail closed for an
    ///     absent entry, which lets terminal cleanup remove state without allowing a late timer to
    ///     recreate it. Returning false means the match is already registered.
    /// </summary>
    public bool RegisterMatching(long matchingId)
    {
        ValidateMatchingId(matchingId);
        return _matchStates.TryAdd(matchingId, new MatchTurnState());
    }

    /// <summary>
    ///     Attempts to claim a due realtime combat turn without waiting. Required waiters have
    ///     priority, and a successful acquisition consumes the due even if later preparation or
    ///     dispatch fails.
    /// </summary>
    public PublicationTurn? TryBeginDueRealtimeTurn(long matchingId)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            if (state.Cleared || state.RequiredWaiters.Count > 0)
                return null;

            ulong now = _getTimestamp();
            if (state.HasRealtimeSchedule &&
                !HasReachedTimestamp(now, state.NextRealtimeEligibleTimestamp))
                return null;

            if (state.ActiveTurnId.HasValue)
            {
                ArmRealtimeDemandIfNeeded(state, now);
                return null;
            }

            if (state.HasPendingRealtimeDemand &&
                state.OrderedWaiters.Count > 0 &&
                IsRealtimeDemandExpired(state, now))
            {
                // The FIFO head owns expiry fallback. Keep the reservation intact until that
                // waiter can clear it and activate atomically under this same gate.
                Monitor.PulseAll(state.Gate);
                return null;
            }

            ClearPendingRealtimeDemand(state);
            return ActivateTurnAndRebaseRealtimeDue(matchingId, state, now);
        }
    }

    /// <summary>
    ///     Waits for the current publication to retire, then claims the required turn. Callers must
    ///     invoke this before entering the match runtime monitor. A terminal cleanup wakes waiters
    ///     and returns <see langword="null"/> instead of resurrecting the cleared state.
    /// </summary>
    public PublicationTurn? BeginRequiredTurn(long matchingId)
    {
        return BeginRequiredBlockingTurn(matchingId);
    }

    /// <summary>
    ///     Waits for the current publication to retire, then claims the same ordered match turn
    ///     without consuming or rebasing the realtime combat due. Periodic countdown publication
    ///     uses this lane so its packet cannot split a combat/settlement bundle.
    /// </summary>
    public PublicationTurn? BeginOrderedTurn(long matchingId)
    {
        return BeginOrderedBlockingTurn(matchingId);
    }

    /// <summary>
    ///     Returns whether the periodic countdown projection differs from the last committed
    ///     second. This is only an optimistic precheck; callers must commit again with the acquired
    ///     ordered turn while the match runtime monitor is held.
    /// </summary>
    public bool NeedsPeriodicCountdownPublication(long matchingId, int remainingSeconds)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return false;

        lock (state.Gate)
        {
            return !state.Cleared &&
                   (!state.HasPeriodicCountdownPublication ||
                    state.LastPeriodicCountdownSeconds != remainingSeconds);
        }
    }

    /// <summary>
    ///     Commits the periodic countdown second for a live ordered turn. The commit precedes
    ///     packet capture, preserving the legacy no-retry boundary when later transport fails.
    /// </summary>
    public bool TryCommitPeriodicCountdownPublication(
        PublicationTurn turn,
        int remainingSeconds)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ValidateOwnedTurn(turn);
        lock (turn.State.Gate)
        {
            ValidateActiveTurnUnderLock(turn);
            if (turn.State.HasPeriodicCountdownPublication &&
                turn.State.LastPeriodicCountdownSeconds == remainingSeconds)
            {
                return false;
            }

            turn.State.LastPeriodicCountdownSeconds = remainingSeconds;
            turn.State.HasPeriodicCountdownPublication = true;
            return true;
        }
    }

    private PublicationTurn? BeginRequiredBlockingTurn(long matchingId)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            if (state.Cleared)
                return null;

            var waiter = new BlockingWaiter();
            LinkedListNode<BlockingWaiter> node = state.RequiredWaiters.AddLast(waiter);
            Monitor.PulseAll(state.Gate);
            try
            {
                while (!state.Cleared &&
                       (state.ActiveTurnId.HasValue ||
                        !ReferenceEquals(state.RequiredWaiters.First, node)))
                {
                    Monitor.Wait(state.Gate);
                }

                if (state.Cleared)
                    return null;

                ulong now = _getTimestamp();
                state.RequiredWaiters.Remove(node);
                ClearPendingRealtimeDemand(state);
                return ActivateTurnAndRebaseRealtimeDue(matchingId, state, now);
            }
            finally
            {
                if (node.List != null)
                    state.RequiredWaiters.Remove(node);
                Monitor.PulseAll(state.Gate);
            }
        }
    }

    private PublicationTurn? BeginOrderedBlockingTurn(long matchingId)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            if (state.Cleared)
                return null;

            var waiter = new BlockingWaiter();
            LinkedListNode<BlockingWaiter> node = state.OrderedWaiters.AddLast(waiter);
            try
            {
                while (!state.Cleared)
                {
                    if (state.ActiveTurnId.HasValue ||
                        state.RequiredWaiters.Count > 0 ||
                        !ReferenceEquals(state.OrderedWaiters.First, node))
                    {
                        Monitor.Wait(state.Gate);
                        continue;
                    }

                    if (!state.HasPendingRealtimeDemand)
                    {
                        state.OrderedWaiters.Remove(node);
                        return ActivateTurn(matchingId, state);
                    }

                    long demandVersion = state.RealtimeDemandVersion;
                    bool expired;
                    try
                    {
                        expired = IsRealtimeDemandExpired(state, _getTimestamp());
                    }
                    catch
                    {
                        if (state.HasPendingRealtimeDemand &&
                            state.RealtimeDemandVersion == demandVersion)
                        {
                            ClearPendingRealtimeDemand(state);
                        }

                        state.OrderedWaiters.Remove(node);
                        return ActivateTurn(matchingId, state);
                    }

                    if (expired)
                    {
                        if (state.HasPendingRealtimeDemand &&
                            state.RealtimeDemandVersion == demandVersion)
                        {
                            ClearPendingRealtimeDemand(state);
                        }

                        state.OrderedWaiters.Remove(node);
                        return ActivateTurn(matchingId, state);
                    }

                    Monitor.Wait(state.Gate, GetRealtimeDemandWaitMilliseconds(state));
                }

                return null;
            }
            finally
            {
                if (node.List != null)
                    state.OrderedWaiters.Remove(node);
                Monitor.PulseAll(state.Gate);
            }
        }
    }

    /// <summary>
    ///     Starts the sole capture scope for a live turn. The scope is execution-context local;
    ///     owner, turn, and phase checks prevent a foreign or already-frozen scope from accepting
    ///     additional steps.
    /// </summary>
    public CaptureScope BeginCapture(PublicationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (_activeCapture.Value != null)
            throw new InvalidOperationException("A combat publication capture is already active in this context.");

        ValidateOwnedTurn(turn);
        lock (turn.State.Gate)
        {
            ValidateActiveTurnUnderLock(turn);
            if (turn.CaptureStarted != 0)
                throw new InvalidOperationException("A combat publication turn can be captured only once.");
            turn.CaptureStarted = 1;
        }

        var frame = new CaptureFrame(this, turn);
        _activeCapture.Value = frame;
        return new CaptureScope(this, frame);
    }

    /// <summary>
    ///     Captures the current packet as immutable recorded wire bytes. Returning false means that
    ///     no live capture owns this execution context and the caller should perform its normal send.
    /// </summary>
    public bool TryCapturePacket(Action<IPacket> sendDirect, IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(sendDirect);
        ArgumentNullException.ThrowIfNull(packet);

        if (_dispatchDepth.Value > 0)
            return false;

        CaptureFrame? frame = _activeCapture.Value;
        if (frame == null || !ReferenceEquals(frame.Owner, this))
            return false;

        return TryCapturePacket(new PacketRecipient(sendDirect), packet);
    }

    /// <summary>
    ///     Captures for an already frozen recipient identity. Tests and future publication adapters
    ///     can retain this wrapper when constructing several ordered packet steps for one recipient.
    /// </summary>
    public bool TryCapturePacket(PacketRecipient recipient, IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(packet);

        // A replay can execute while another match has an ambient capture in the inherited
        // ExecutionContext. Suppression makes the recipient's normal Send override fall through
        // to its direct transport path instead of recursively capturing replayed bytes.
        if (_dispatchDepth.Value > 0)
            return false;

        CaptureFrame? frame = _activeCapture.Value;
        if (frame == null ||
            !ReferenceEquals(frame.Owner, this) ||
            packet is not Packet concretePacket)
            return false;

        // UserToken records the size immediately before socket dispatch. Capture does the same now
        // so Packet.CreateForSending can validate and replay an exact, self-contained wire image.
        concretePacket.RecordSize();
        byte[] wireBytes = concretePacket.ToBytes();

        lock (frame.Gate)
        {
            if (frame.Phase != CapturePhase.Capturing)
                return false;

            frame.Steps.Add(new WirePublicationStep(
                recipient,
                ImmutableArray.CreateRange(wireBytes),
                frame.CurrentBoundary));
            return true;
        }
    }

    /// <summary>
    ///     Appends a frozen deferred action at the current packet-order position. The caller owns
    ///     deep-copying any values closed over by the action before appending it.
    /// </summary>
    public void AppendDeferredStep(Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        CaptureFrame frame = GetCapturingFrame();
        lock (frame.Gate)
        {
            EnsureCapturing(frame);
            frame.Steps.Add(new DeferredPublicationStep(dispatch, frame.CurrentBoundary));
        }
    }

    /// <summary>
    ///     Marks a contiguous group whose first dispatch failure is reported, whose remaining group
    ///     steps are skipped, and after which the surrounding publication continues.
    /// </summary>
    public IDisposable BeginBestEffortGroup(Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(reportFailure);
        CaptureFrame frame = GetCapturingFrame();
        lock (frame.Gate)
        {
            EnsureCapturing(frame);
            if (frame.CurrentBoundary != null)
                throw new InvalidOperationException("Nested combat publication failure groups are not supported.");

            var boundary = new BestEffortBoundary(
                Interlocked.Increment(ref _nextBoundaryId),
                reportFailure);
            frame.CurrentBoundary = boundary;
            return new BestEffortGroupScope(this, frame, boundary);
        }
    }

    /// <summary>
    ///     Opens a best-effort boundary only when this execution context is preparing a combat
    ///     publication. Legacy direct-send callers receive <see langword="null"/> and keep their
    ///     existing synchronous try/catch boundary.
    /// </summary>
    public IDisposable? TryBeginBestEffortGroup(Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (_dispatchDepth.Value > 0)
            return null;

        CaptureFrame? frame = _activeCapture.Value;
        if (frame == null || !ReferenceEquals(frame.Owner, this))
            return null;

        lock (frame.Gate)
        {
            if (frame.Phase != CapturePhase.Capturing)
                return null;
            if (frame.CurrentBoundary != null)
                throw new InvalidOperationException("Nested combat publication failure groups are not supported.");

            var boundary = new BestEffortBoundary(
                Interlocked.Increment(ref _nextBoundaryId),
                reportFailure);
            frame.CurrentBoundary = boundary;
            return new BestEffortGroupScope(this, frame, boundary);
        }
    }

    /// <summary>
    ///     Replays the frozen plan and always retires the turn. A default-step failure aborts and is
    ///     propagated; a best-effort-group failure skips that group and continues.
    /// </summary>
    public void DispatchAndRetire(PublicationTurn turn, PublicationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(plan);
        ValidateOwnedTurn(turn);
        if (turn.MatchingId != plan.MatchingId || turn.TurnId != plan.TurnId)
            throw new InvalidOperationException("The combat publication plan does not belong to this turn.");

        BeginDispatch(turn);

        try
        {
            DispatchPlan(plan);
        }
        finally
        {
            CompleteDispatch(turn);
        }
    }

    /// <summary>
    ///     Terminal cleanup marks the registered state cleared, detaches and wakes both waiter
    ///     queues, then
    ///     removes the entry. Stale holders still observe the cleared state; late begins fail closed
    ///     because only match-start registration may create an entry.
    /// </summary>
    public void ClearMatching(long matchingId)
    {
        if (matchingId <= 0)
            return;

        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return;

        lock (state.Gate)
        {
            state.Cleared = true;
            state.RequiredWaiters.Clear();
            state.OrderedWaiters.Clear();
            ClearPendingRealtimeDemand(state);
            Monitor.PulseAll(state.Gate);
        }

        _matchStates.TryRemove(new KeyValuePair<long, MatchTurnState>(matchingId, state));
    }

    internal PublicationDiagnostics? Inspect(long matchingId)
    {
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            return new PublicationDiagnostics(
                state.ActiveTurnId.HasValue,
                state.RequiredWaiters.Count,
                state.OrderedWaiters.Count,
                state.HasPendingRealtimeDemand,
                state.RealtimeDemandVersion,
                state.RealtimeDemandExpiresAt,
                state.Cleared);
        }
    }

    private void ArmRealtimeDemandIfNeeded(MatchTurnState state, ulong observedAt)
    {
        if (state.HasPendingRealtimeDemand)
            return;

        ulong stopwatchNow = unchecked((ulong)Stopwatch.GetTimestamp());
        state.HasPendingRealtimeDemand = true;
        state.RealtimeDemandVersion = unchecked(state.RealtimeDemandVersion + 1);
        state.RealtimeDemandExpiresAt =
            unchecked(observedAt + _realtimeHandoffGraceTicks);
        state.RealtimeDemandStopwatchExpiresAt =
            unchecked(stopwatchNow + _realtimeHandoffGraceStopwatchTicks);
        Monitor.PulseAll(state.Gate);
    }

    private static void ClearPendingRealtimeDemand(MatchTurnState state)
    {
        state.HasPendingRealtimeDemand = false;
        state.RealtimeDemandExpiresAt = 0;
        state.RealtimeDemandStopwatchExpiresAt = 0;
    }

    private static bool IsRealtimeDemandExpired(MatchTurnState state, ulong timestamp)
    {
        if (!state.HasPendingRealtimeDemand)
            return false;

        ulong stopwatchNow = unchecked((ulong)Stopwatch.GetTimestamp());
        return HasReachedTimestamp(timestamp, state.RealtimeDemandExpiresAt) ||
               HasReachedTimestamp(stopwatchNow, state.RealtimeDemandStopwatchExpiresAt);
    }

    private static int GetRealtimeDemandWaitMilliseconds(MatchTurnState state)
    {
        ulong stopwatchNow = unchecked((ulong)Stopwatch.GetTimestamp());
        if (HasReachedTimestamp(stopwatchNow, state.RealtimeDemandStopwatchExpiresAt))
            return 1;

        ulong remainingTicks =
            unchecked(state.RealtimeDemandStopwatchExpiresAt - stopwatchNow);
        UInt128 milliseconds =
            ((UInt128)remainingTicks * 1_000 + checked((ulong)Stopwatch.Frequency) - 1) /
            checked((ulong)Stopwatch.Frequency);
        if (milliseconds == 0)
            return 1;
        return milliseconds > int.MaxValue
            ? int.MaxValue
            : (int)milliseconds;
    }

    private PublicationTurn ActivateTurnAndRebaseRealtimeDue(
        long matchingId,
        MatchTurnState state,
        ulong startedAt)
    {
        long turnId = Interlocked.Increment(ref _nextTurnId);
        state.ActiveTurnId = turnId;
        state.NextRealtimeEligibleTimestamp = unchecked(startedAt + _realtimeIntervalTicks);
        state.HasRealtimeSchedule = true;
        return new PublicationTurn(this, matchingId, turnId, state);
    }

    private PublicationTurn ActivateTurn(long matchingId, MatchTurnState state)
    {
        long turnId = Interlocked.Increment(ref _nextTurnId);
        state.ActiveTurnId = turnId;
        return new PublicationTurn(this, matchingId, turnId, state);
    }

    private static bool HasReachedTimestamp(ulong timestamp, ulong due) =>
        unchecked((long)(timestamp - due)) >= 0;

    private CaptureFrame GetCapturingFrame()
    {
        CaptureFrame? frame = _activeCapture.Value;
        if (frame == null || !ReferenceEquals(frame.Owner, this))
            throw new InvalidOperationException("No combat publication capture is active in this context.");
        return frame;
    }

    private static void EnsureCapturing(CaptureFrame frame)
    {
        if (frame.Phase != CapturePhase.Capturing)
            throw new InvalidOperationException("The combat publication capture is no longer writable.");
    }

    private PublicationPlan FreezeCapture(CaptureFrame frame)
    {
        if (!ReferenceEquals(frame.Owner, this))
            throw new InvalidOperationException("The combat publication capture belongs to another coordinator.");

        ImmutableArray<PublicationStep> steps;
        lock (frame.Gate)
        {
            EnsureCapturing(frame);
            if (frame.CurrentBoundary != null)
                throw new InvalidOperationException("Close the best-effort group before freezing its publication.");

            frame.Phase = CapturePhase.Frozen;
            steps = frame.Steps.ToImmutable();
        }

        if (ReferenceEquals(_activeCapture.Value, frame))
            _activeCapture.Value = null;
        return new PublicationPlan(frame.Turn.MatchingId, frame.Turn.TurnId, steps);
    }

    private void AbandonCapture(CaptureFrame frame)
    {
        if (!ReferenceEquals(frame.Owner, this))
            return;

        lock (frame.Gate)
        {
            if (frame.Phase == CapturePhase.Capturing)
                frame.Phase = CapturePhase.Abandoned;
        }

        if (ReferenceEquals(_activeCapture.Value, frame))
            _activeCapture.Value = null;
    }

    private void CloseBestEffortGroup(CaptureFrame frame, BestEffortBoundary boundary)
    {
        if (!ReferenceEquals(frame.Owner, this))
            throw new InvalidOperationException("The failure group belongs to another coordinator.");

        lock (frame.Gate)
        {
            if (frame.Phase != CapturePhase.Capturing)
                return;
            if (!ReferenceEquals(frame.CurrentBoundary, boundary))
                throw new InvalidOperationException("The active failure group does not match this scope.");
            frame.CurrentBoundary = null;
        }
    }

    private void DispatchPlan(PublicationPlan plan)
    {
        int previousDispatchDepth = _dispatchDepth.Value;
        _dispatchDepth.Value = previousDispatchDepth + 1;
        try
        {
            int index = 0;
            while (index < plan.Steps.Length)
            {
                PublicationStep step = plan.Steps[index];
                try
                {
                    step.Dispatch();
                    index++;
                }
                catch (Exception ex) when (step.Boundary != null)
                {
                    BestEffortBoundary boundary = step.Boundary;
                    try
                    {
                        boundary.ReportFailure(ex);
                    }
                    catch (Exception reportingFailure)
                    {
                        throw new AggregateException(
                            "Combat publication and best-effort failure reporting both failed.",
                            ex,
                            reportingFailure);
                    }

                    index++;
                    while (index < plan.Steps.Length &&
                           ReferenceEquals(plan.Steps[index].Boundary, boundary))
                    {
                        index++;
                    }
                }
            }
        }
        finally
        {
            _dispatchDepth.Value = previousDispatchDepth;
        }
    }

    private static void ValidateActiveTurnUnderLock(PublicationTurn turn)
    {
        if (turn.Lifecycle != TurnLifecycle.Active ||
            turn.State.Cleared ||
            turn.State.ActiveTurnId != turn.TurnId)
        {
            throw new InvalidOperationException("The combat publication turn is no longer active.");
        }
    }

    private void ValidateOwnedTurn(PublicationTurn turn)
    {
        if (!ReferenceEquals(turn.Owner, this))
            throw new InvalidOperationException("The combat publication turn belongs to another coordinator.");
    }

    private void BeginDispatch(PublicationTurn turn)
    {
        ValidateOwnedTurn(turn);
        lock (turn.State.Gate)
        {
            ValidateActiveTurnUnderLock(turn);
            turn.Lifecycle = TurnLifecycle.Dispatching;
        }
    }

    private void CompleteDispatch(PublicationTurn turn)
    {
        lock (turn.State.Gate)
        {
            if (turn.Lifecycle != TurnLifecycle.Dispatching ||
                turn.State.ActiveTurnId != turn.TurnId)
            {
                throw new InvalidOperationException("The combat publication dispatch lifecycle is inconsistent.");
            }

            turn.Lifecycle = TurnLifecycle.Retired;
            turn.State.ActiveTurnId = null;
            Monitor.PulseAll(turn.State.Gate);
        }
    }

    private void Retire(PublicationTurn turn)
    {
        ValidateOwnedTurn(turn);
        lock (turn.State.Gate)
        {
            if (turn.Lifecycle is TurnLifecycle.Retired or TurnLifecycle.Dispatching)
                return;

            if (turn.State.ActiveTurnId != turn.TurnId)
            {
                if (turn.State.Cleared)
                {
                    turn.Lifecycle = TurnLifecycle.Retired;
                    return;
                }

                throw new InvalidOperationException("A different combat publication turn is active.");
            }

            turn.Lifecycle = TurnLifecycle.Retired;
            turn.State.ActiveTurnId = null;
            Monitor.PulseAll(turn.State.Gate);
        }
    }

    private static void ValidateMatchingId(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
    }

    internal sealed class PublicationTurn : IDisposable
    {
        internal readonly SwarmCombatPublicationCoordinator Owner;
        internal readonly MatchTurnState State;
        internal int CaptureStarted;
        internal TurnLifecycle Lifecycle;

        internal PublicationTurn(
            SwarmCombatPublicationCoordinator owner,
            long matchingId,
            long turnId,
            MatchTurnState state)
        {
            Owner = owner;
            MatchingId = matchingId;
            TurnId = turnId;
            State = state;
        }

        public long MatchingId { get; }
        internal long TurnId { get; }

        public void Dispose() => Owner.Retire(this);
    }

    internal sealed class CaptureScope : IDisposable
    {
        private readonly SwarmCombatPublicationCoordinator _owner;
        private readonly CaptureFrame _frame;
        private int _closed;

        internal CaptureScope(SwarmCombatPublicationCoordinator owner, CaptureFrame frame)
        {
            _owner = owner;
            _frame = frame;
        }

        public PublicationPlan Freeze()
        {
            if (Interlocked.CompareExchange(ref _closed, -1, 0) != 0)
                throw new ObjectDisposedException(nameof(CaptureScope));

            try
            {
                PublicationPlan plan = _owner.FreezeCapture(_frame);
                Volatile.Write(ref _closed, 1);
                return plan;
            }
            catch
            {
                Volatile.Write(ref _closed, 0);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                _owner.AbandonCapture(_frame);
        }
    }

    internal sealed class PublicationPlan
    {
        internal PublicationPlan(long matchingId, long turnId, ImmutableArray<PublicationStep> steps)
        {
            MatchingId = matchingId;
            TurnId = turnId;
            Steps = steps;
        }

        public long MatchingId { get; }
        public int Count => Steps.Length;
        internal long TurnId { get; }
        internal ImmutableArray<PublicationStep> Steps { get; }
    }

    internal sealed class PacketRecipient
    {
        private readonly Action<IPacket> _send;

        /// <summary>
        ///     Wraps the recipient's normal send entrypoint. During plan replay the coordinator
        ///     suppresses ambient capture, so an override must fall through to its direct transport
        ///     send when <see cref="TryCapturePacket"/> returns <see langword="false"/>.
        /// </summary>
        public PacketRecipient(Action<IPacket> send)
        {
            ArgumentNullException.ThrowIfNull(send);
            _send = send;
        }

        internal void Send(IPacket packet) => _send(packet);
    }

    internal readonly record struct PublicationDiagnostics(
        bool HasActiveTurn,
        int RequiredWaiterCount,
        int OrderedWaiterCount,
        bool HasPendingRealtimeDemand,
        long RealtimeDemandVersion,
        ulong RealtimeDemandExpiresAt,
        bool IsCleared);

    internal sealed class MatchTurnState
    {
        public readonly object Gate = new();
        public readonly LinkedList<BlockingWaiter> RequiredWaiters = new();
        public readonly LinkedList<BlockingWaiter> OrderedWaiters = new();
        public long? ActiveTurnId;
        public ulong NextRealtimeEligibleTimestamp;
        public bool HasRealtimeSchedule;
        public bool HasPendingRealtimeDemand;
        public long RealtimeDemandVersion;
        public ulong RealtimeDemandExpiresAt;
        public ulong RealtimeDemandStopwatchExpiresAt;
        public int LastPeriodicCountdownSeconds;
        public bool HasPeriodicCountdownPublication;
        public bool Cleared;
    }

    internal sealed class BlockingWaiter;

    internal sealed class CaptureFrame(
        SwarmCombatPublicationCoordinator owner,
        PublicationTurn turn)
    {
        public readonly object Gate = new();
        public readonly ImmutableArray<PublicationStep>.Builder Steps =
            ImmutableArray.CreateBuilder<PublicationStep>();
        public SwarmCombatPublicationCoordinator Owner { get; } = owner;
        public PublicationTurn Turn { get; } = turn;
        public CapturePhase Phase { get; set; } = CapturePhase.Capturing;
        public BestEffortBoundary? CurrentBoundary { get; set; }
    }

    internal enum CapturePhase
    {
        Capturing,
        Frozen,
        Abandoned
    }

    internal enum TurnLifecycle
    {
        Active,
        Dispatching,
        Retired
    }

    internal abstract class PublicationStep(BestEffortBoundary? boundary)
    {
        public BestEffortBoundary? Boundary { get; } = boundary;
        public abstract void Dispatch();
    }

    private sealed class WirePublicationStep(
        PacketRecipient recipient,
        ImmutableArray<byte> wireBytes,
        BestEffortBoundary? boundary)
        : PublicationStep(boundary)
    {
        public override void Dispatch()
        {
            using Packet packet = Packet.CreateForSending(wireBytes.ToArray());
            recipient.Send(packet);
        }
    }

    private sealed class DeferredPublicationStep(
        Action dispatch,
        BestEffortBoundary? boundary)
        : PublicationStep(boundary)
    {
        public override void Dispatch() => dispatch();
    }

    internal sealed class BestEffortBoundary(long boundaryId, Action<Exception> reportFailure)
    {
        // Kept for diagnostics and debugger readability even though reference identity owns grouping.
        public long BoundaryId { get; } = boundaryId;
        public void ReportFailure(Exception failure) => reportFailure(failure);
    }

    private sealed class BestEffortGroupScope(
        SwarmCombatPublicationCoordinator owner,
        CaptureFrame frame,
        BestEffortBoundary boundary) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.CloseBestEffortGroup(frame, boundary);
        }
    }
}
