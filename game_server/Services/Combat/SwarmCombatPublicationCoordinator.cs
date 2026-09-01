using System.Collections.Concurrent;
using System.Collections.Immutable;
using network.interfaces;
using network.packets;

namespace game_server.services;

/// <summary>
///     Owns one prepare-to-dispatch publication turn per match. A realtime caller may coalesce
///     while a turn is active, whereas a required caller waits outside the match runtime monitor.
///     Captured packet bytes and deferred steps are replayed in their original call order.
/// </summary>
internal sealed class SwarmCombatPublicationCoordinator
{
    private readonly ConcurrentDictionary<long, MatchTurnState> _matchStates = new();
    private readonly AsyncLocal<CaptureFrame?> _activeCapture = new();
    private readonly AsyncLocal<int> _dispatchDepth = new();
    private long _nextTurnId;
    private long _nextBoundaryId;

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
    ///     Attempts to claim a realtime combat turn without waiting. Required waiters have priority
    ///     so a 50 ms callback cannot repeatedly steal the turn from a settlement publication.
    /// </summary>
    public PublicationTurn? TryBeginRealtimeTurn(long matchingId)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            if (state.Cleared || state.ActiveTurnId.HasValue || state.RequiredWaiterCount > 0)
                return null;

            return ActivateTurn(matchingId, state);
        }
    }

    /// <summary>
    ///     Waits for the current publication to retire, then claims the required turn. Callers must
    ///     invoke this before entering the match runtime monitor. A terminal cleanup wakes waiters
    ///     and returns <see langword="null"/> instead of resurrecting the cleared state.
    /// </summary>
    public PublicationTurn? BeginRequiredTurn(long matchingId)
    {
        ValidateMatchingId(matchingId);
        if (!_matchStates.TryGetValue(matchingId, out MatchTurnState? state))
            return null;

        lock (state.Gate)
        {
            if (state.Cleared)
                return null;

            state.RequiredWaiterCount++;
            try
            {
                while (state.ActiveTurnId.HasValue && !state.Cleared)
                    Monitor.Wait(state.Gate);

                if (state.Cleared)
                    return null;

                return ActivateTurn(matchingId, state);
            }
            finally
            {
                state.RequiredWaiterCount--;
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
    ///     Terminal cleanup marks the registered state cleared, wakes its required waiters, then
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
                state.RequiredWaiterCount,
                state.Cleared);
        }
    }

    private PublicationTurn ActivateTurn(long matchingId, MatchTurnState state)
    {
        long turnId = Interlocked.Increment(ref _nextTurnId);
        state.ActiveTurnId = turnId;
        return new PublicationTurn(this, matchingId, turnId, state);
    }

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
        bool IsCleared);

    internal sealed class MatchTurnState
    {
        public readonly object Gate = new();
        public long? ActiveTurnId;
        public int RequiredWaiterCount;
        public bool Cleared;
    }

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
