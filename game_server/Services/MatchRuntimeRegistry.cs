using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using StoreMatchRuntime = game_server.services.MatchRuntime;

namespace game_server.services;

/// <summary>
///     Owns the serialization boundary and terminal lifecycle for each in-memory match.
///     Finalization freezes a tokenized before-callback claim at execution depth zero, runs that
///     snapshot outside the match monitor, then validates the same claim for component cleanup and
///     terminal commit under the monitor. Post-commit callbacks run outside it.
///     Recently completed matching ids are tombstoned within a bounded retention window so a
///     late timer or client packet cannot immediately recreate state after cleanup.
/// </summary>
public sealed class MatchRuntimeRegistry
{
    private const int CompletedRetentionLimit = 4096;
    private readonly ConcurrentDictionary<long, MatchRuntime> _activeRuntimes = new();
    private readonly ConcurrentDictionary<long, byte> _completedMatchingIds = new();
    private readonly ConcurrentQueue<long> _completedOrder = new();
    private readonly object _runtimeCreationGate = new();
    private Action<long>? _runtimeInitializer;
    private MatchRuntimeStore? _store;
    private bool _runtimeUseStarted;

    public int ActiveCount => _activeRuntimes.Count;

    /// <summary>
    ///     Configures a component-registration hook before the first runtime is used. The hook is
    ///     invoked once per accepted match runtime while its lifecycle monitor is held, before any
    ///     execution, lease, owner bind, or finalization can observe that runtime.
    /// </summary>
    internal void SetRuntimeInitializer(Action<long> runtimeInitializer)
    {
        ArgumentNullException.ThrowIfNull(runtimeInitializer);
        lock (_runtimeCreationGate)
        {
            if (_runtimeUseStarted)
                throw new InvalidOperationException("The match runtime initializer must be configured before use.");
            if (_runtimeInitializer != null)
                throw new InvalidOperationException("The match runtime initializer is already configured.");

            Volatile.Write(ref _runtimeInitializer, runtimeInitializer);
        }
    }

    /// <summary>
    ///     #331 이행 셈: 등록소 런타임의 모니터를 <see cref="MatchRuntimeStore"/>의 런타임과 공유해
    ///     두 경로가 같은 잠금 객체로 직렬화된다. 등록소 커밋은 저장소 런타임도 터미널로 표시한다.
    /// </summary>
    internal void AttachStore(MatchRuntimeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_runtimeCreationGate)
        {
            if (_runtimeUseStarted)
                throw new InvalidOperationException("The match runtime store must be attached before use.");

            _store = store;
        }
    }

    public bool TryExecute(long matchingId, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return false;

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        FinalizationClaim? finalizationClaim = null;
        ExceptionDispatchInfo? actionFailure = null;
        ExceptionDispatchInfo? finalizationFailure = null;
        lock (runtime.SyncRoot)
        {
            if (_completedMatchingIds.ContainsKey(matchingId))
            {
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                return false;
            }

            runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
            if (!runtime.TryBeginExecution())
                return false;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                actionFailure = ExceptionDispatchInfo.Capture(ex);
            }

            try
            {
                finalizationClaim = runtime.EndExecution();
            }
            catch (Exception ex)
            {
                finalizationFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (finalizationFailure == null && finalizationClaim != null)
        {
            try
            {
                CompleteFinalization(matchingId, runtime, finalizationClaim);
            }
            catch (Exception ex)
            {
                finalizationFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        ThrowIfFailures(
            actionFailure,
            finalizationFailure,
            "Match action and deferred finalization both failed.");
        return true;
    }

    /// <summary>
    ///     Attempts to register terminal cleanup and an optional post-commit continuation.
    /// </summary>
    /// <returns>
    ///     <list type="table">
    ///         <listheader>
    ///             <term>Result and state</term>
    ///             <description>Contract</description>
    ///         </listheader>
    ///         <item>
    ///             <term>true / Active winner</term>
    ///             <description>The caller owns cleanup. Cleanup runs now or after outstanding leases drain;
    ///             post runs once after commit outside the monitor.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Active rejected or invalid id</term>
    ///             <description>Neither cleanup nor post runs.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Finalizing</term>
    ///             <description>The caller's predicate and cleanup do not run. Its non-null post is attached
    ///             to pending work and runs once after that commit.</description>
    ///         </item>
    ///         <item>
    ///             <term>false / Completed or tombstoned</term>
    ///             <description>The caller's predicate and cleanup do not run. Its non-null post runs once
    ///             immediately outside the monitor.</description>
    ///         </item>
    ///     </list>
    /// </returns>
    public bool TryFinalize(
        long matchingId,
        Func<bool> canFinalize,
        Action cleanup,
        Action? afterFinalized = null)
    {
        return TryFinalize(
            matchingId,
            canFinalize,
            beforeFinalized: null,
            cleanup,
            afterFinalized);
    }

    /// <summary>
    ///     Attempts to register terminal cleanup with ordered hooks that run immediately before
    ///     the winner-owned component cleanup and after the terminal commit, respectively.
    ///     At execution depth zero, the runtime freezes the before-hook snapshot while holding its
    ///     lifecycle monitor, runs that snapshot outside the monitor, then validates the same
    ///     work/token before component cleanup and commit under the monitor.
    ///     A losing caller may attach only its hooks to pending work; it never contributes another
    ///     component cleanup plan. Once the before phase has started or completion has won the race,
    ///     a late before hook is dropped while a late after hook retains the public post-commit contract.
    ///     Before-hook failures do not prevent cleanup, commit, or post dispatch and are rethrown
    ///     after those phases. A component-cleanup failure resets the runtime to Active, skips post,
    ///     and abandons the frozen before snapshot. A post failure leaves the committed runtime terminal
    ///     and does not replay callbacks.
    ///     A later attempt after failed cleanup may explicitly register new hooks.
    /// </summary>
    internal bool TryFinalize(
        long matchingId,
        Func<bool> canFinalize,
        Action? beforeFinalized,
        Action cleanup,
        Action? afterFinalized)
    {
        ArgumentNullException.ThrowIfNull(canFinalize);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (matchingId <= 0)
            return false;

        Action? postFinalization = null;
        if (_completedMatchingIds.ContainsKey(matchingId))
        {
            afterFinalized?.Invoke();
            return false;
        }

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        FinalizationClaim? finalizationClaim = null;
        bool wonFinalization;
        lock (runtime.SyncRoot)
        {
            if (_completedMatchingIds.ContainsKey(matchingId))
            {
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                postFinalization = afterFinalized;
                wonFinalization = false;
            }
            else
            {
                runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
                // A caller that already captured terminal publication or post-commit work may
                // arrive after another execution or operation won the transition. Attach only
                // those hooks to pending work; its predicate and component cleanup stay skipped.
                if (runtime.TryAttachFinalizationHooks(beforeFinalized, afterFinalized))
                {
                    wonFinalization = false;
                }
                else if (!canFinalize())
                {
                    // Evaluate state-dependent predicates under the same lifecycle lock as the
                    // Active -> Finalizing transition. Connection registration uses this lock too.
                    wonFinalization = false;
                }
                else
                {
                    var finalization = new FinalizationWork(cleanup, beforeFinalized, afterFinalized);
                    wonFinalization = runtime.TryBeginFinalization(
                        finalization,
                        out finalizationClaim);
                }
            }
        }

        if (finalizationClaim != null)
            CompleteFinalization(matchingId, runtime, finalizationClaim);
        postFinalization?.Invoke();
        return wonFinalization;
    }

    public bool IsTerminal(long matchingId)
    {
        if (matchingId <= 0)
            return false;
        if (_completedMatchingIds.ContainsKey(matchingId))
            return true;

        return _activeRuntimes.TryGetValue(matchingId, out var runtime) && runtime.IsTerminal;
    }

    /// <summary>
    ///     Acquires a terminal-lifecycle lease for asynchronous work. The lease does not hold
    ///     a monitor across await, but final cleanup is deferred until every acquired lease
    ///     and synchronous execution has completed.
    /// </summary>
    public IDisposable? TryAcquireOperation(long matchingId, Action onAcquired) =>
        TryAcquireOperationCore(matchingId, onAcquired, waitForRuntimeMonitor: true);

    /// <summary>
    ///     Attempts to acquire a terminal-lifecycle lease without waiting for the match monitor.
    ///     Timer callers can drop only the contended match pulse and continue scheduling siblings.
    /// </summary>
    public IDisposable? TryAcquireOperationIfAvailable(long matchingId, Action onAcquired) =>
        TryAcquireOperationCore(matchingId, onAcquired, waitForRuntimeMonitor: false);

    private IDisposable? TryAcquireOperationCore(
        long matchingId,
        Action onAcquired,
        bool waitForRuntimeMonitor)
    {
        ArgumentNullException.ThrowIfNull(onAcquired);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return null;

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        FinalizationClaim? finalizationClaim = null;
        MatchRuntimeOperation? operation = null;
        ExceptionDispatchInfo? acquisitionFailure = null;
        ExceptionDispatchInfo? finalizationFailure = null;
        bool lockTaken = false;
        try
        {
            if (waitForRuntimeMonitor)
                Monitor.Enter(runtime.SyncRoot, ref lockTaken);
            else
                Monitor.TryEnter(runtime.SyncRoot, ref lockTaken);
            if (!lockTaken)
                return null;

            if (_completedMatchingIds.ContainsKey(matchingId))
            {
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                return null;
            }

            runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
            if (!runtime.TryBeginExecution())
                return null;

            try
            {
                // Connection registration can be supplied here so a no-human finalizer cannot
                // slip between acquiring the lifecycle lease and publishing the live session.
                onAcquired();
            }
            catch (Exception ex)
            {
                acquisitionFailure = ExceptionDispatchInfo.Capture(ex);
            }

            if (acquisitionFailure == null)
            {
                operation = new MatchRuntimeOperation(this, matchingId, runtime);
            }
            else
            {
                try
                {
                    finalizationClaim = runtime.EndExecution();
                }
                catch (Exception ex)
                {
                    finalizationFailure = ExceptionDispatchInfo.Capture(ex);
                }
            }
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(runtime.SyncRoot);
        }

        if (finalizationFailure == null && finalizationClaim != null)
        {
            try
            {
                CompleteFinalization(matchingId, runtime, finalizationClaim);
            }
            catch (Exception ex)
            {
                finalizationFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        ThrowIfFailures(
            acquisitionFailure,
            finalizationFailure,
            "Match operation acquisition and deferred finalization both failed.");
        return operation;
    }

    private void RememberCompleted(long matchingId)
    {
        if (!_completedMatchingIds.TryAdd(matchingId, 0))
            return;

        _completedOrder.Enqueue(matchingId);
        while (_completedMatchingIds.Count > CompletedRetentionLimit &&
               _completedOrder.TryDequeue(out long expiredMatchingId))
        {
            _completedMatchingIds.TryRemove(expiredMatchingId, out _);
        }
    }

    private void CompleteFinalization(
        long matchingId,
        MatchRuntime runtime,
        FinalizationClaim claim)
    {
        ExceptionDispatchInfo? beforeFailure = null;
        try
        {
            claim.RunBeforeFinalized();
        }
        catch (Exception ex)
        {
            beforeFailure = ExceptionDispatchInfo.Capture(ex);
        }

        Action? afterFinalized = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        try
        {
            lock (runtime.SyncRoot)
            {
                runtime.BeginCleanup(claim);
                try
                {
                    claim.Work.ComponentCleanup();
                }
                catch
                {
                    runtime.ResetAfterFailedFinalization(claim);
                    throw;
                }

                afterFinalized = runtime.Complete(claim);
                RememberCompleted(matchingId);
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                _store?.Remove(matchingId);
            }
        }
        catch (Exception ex)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(ex);
        }

        if (cleanupFailure != null)
        {
            ThrowIfFailures(
                beforeFailure,
                cleanupFailure,
                "Pre-finalization callbacks and component cleanup both failed.");
            return;
        }

        ExceptionDispatchInfo? postFailure = null;
        try
        {
            afterFinalized?.Invoke();
        }
        catch (Exception ex)
        {
            postFailure = ExceptionDispatchInfo.Capture(ex);
        }

        ThrowIfFailures(
            beforeFailure,
            postFailure,
            "Pre-finalization and post-finalization callbacks both failed.");
    }

    private void ReleaseOperation(long matchingId, MatchRuntime runtime)
    {
        FinalizationClaim? finalizationClaim;
        lock (runtime.SyncRoot)
        {
            finalizationClaim = runtime.EndExecution();
        }

        if (finalizationClaim != null)
            CompleteFinalization(matchingId, runtime, finalizationClaim);
    }

    private static void ThrowIfFailures(
        ExceptionDispatchInfo? primaryFailure,
        ExceptionDispatchInfo? secondaryFailure,
        string aggregateMessage)
    {
        if (primaryFailure != null && secondaryFailure != null)
        {
            throw new AggregateException(
                aggregateMessage,
                primaryFailure.SourceException,
                secondaryFailure.SourceException);
        }

        if (primaryFailure != null)
            primaryFailure.Throw();
        if (secondaryFailure != null)
            secondaryFailure.Throw();
    }

    private static void InvokeFinalizationCallbacks(
        IReadOnlyList<Action> callbacks,
        string aggregateMessage)
    {
        List<Exception>? failures = null;
        foreach (Action callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
            }
        }

        if (failures is [Exception singleFailure])
            ExceptionDispatchInfo.Capture(singleFailure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(aggregateMessage, failures);
    }

    private enum FinalizationPhase
    {
        Pending,
        BeforeRunning,
        BeforeCompleted,
        CleanupRunning,
        Committed
    }

    private sealed class FinalizationClaim(
        FinalizationWork work,
        long token,
        Action[] beforeFinalized)
    {
        public FinalizationWork Work { get; } = work;
        public long Token { get; } = token;

        public void RunBeforeFinalized()
        {
            InvokeFinalizationCallbacks(
                beforeFinalized,
                "Multiple pre-finalization callbacks failed.");
        }
    }

    private sealed class FinalizationWork
    {
        // The lists, token, and phase are touched only while the owning MatchRuntime.SyncRoot
        // is held. A claim freezes the before callbacks so the completion owner can run them
        // outside the monitor; commit likewise freezes the post callbacks for outside dispatch.
        private readonly List<Action> _beforeFinalized = [];
        private readonly List<Action> _afterFinalized = [];
        private FinalizationPhase _phase = FinalizationPhase.Pending;

        public FinalizationWork(
            Action componentCleanup,
            Action? beforeFinalized,
            Action? afterFinalized)
        {
            ComponentCleanup = componentCleanup;
            AttachBeforeFinalized(beforeFinalized);
            AttachAfterFinalized(afterFinalized);
        }

        public Action ComponentCleanup { get; }
        public long Token { get; private set; }

        public void AttachHooks(Action? beforeFinalized, Action? afterFinalized)
        {
            if (_phase == FinalizationPhase.Pending && beforeFinalized != null)
                _beforeFinalized.Add(beforeFinalized);
            if (_phase != FinalizationPhase.Committed && afterFinalized != null)
                _afterFinalized.Add(afterFinalized);
        }

        public void BindToken(long token)
        {
            if (token == 0 || Token != 0 || _phase != FinalizationPhase.Pending)
                throw new InvalidOperationException("Finalization work cannot be rebound.");

            Token = token;
        }

        public FinalizationClaim? TryClaim()
        {
            if (_phase != FinalizationPhase.Pending)
                return null;
            if (Token == 0)
                throw new InvalidOperationException("Finalization work must be bound before it is claimed.");

            _phase = FinalizationPhase.BeforeRunning;
            return new FinalizationClaim(this, Token, _beforeFinalized.ToArray());
        }

        public void BeginCleanup(long token)
        {
            ValidateTokenAndPhase(token, FinalizationPhase.BeforeRunning);
            _phase = FinalizationPhase.BeforeCompleted;
            _phase = FinalizationPhase.CleanupRunning;
        }

        public Action? CommitAndCreateAfterFinalizedAction(long token)
        {
            ValidateTokenAndPhase(token, FinalizationPhase.CleanupRunning);
            _phase = FinalizationPhase.Committed;
            if (_afterFinalized.Count == 0)
                return null;

            Action[] callbacks = _afterFinalized.ToArray();
            return () => InvokeFinalizationCallbacks(
                callbacks,
                "Multiple post-finalization callbacks failed.");
        }

        public void ResetAfterFailedCleanup(long token)
        {
            ValidateTokenAndPhase(token, FinalizationPhase.CleanupRunning);
            // This work is abandoned when the runtime returns to Active. Keeping the phase after
            // before completion makes it impossible to claim and replay its frozen callbacks.
            _phase = FinalizationPhase.BeforeCompleted;
        }

        private void AttachBeforeFinalized(Action? beforeFinalized)
        {
            if (beforeFinalized != null)
                _beforeFinalized.Add(beforeFinalized);
        }

        private void AttachAfterFinalized(Action? afterFinalized)
        {
            if (afterFinalized != null)
                _afterFinalized.Add(afterFinalized);
        }

        private void ValidateTokenAndPhase(long token, FinalizationPhase expectedPhase)
        {
            if (Token != token || _phase != expectedPhase)
            {
                throw new InvalidOperationException(
                    $"Finalization phase mismatch. Expected={expectedPhase}, Actual={_phase}.");
            }
        }
    }

    private MatchRuntime GetOrCreateRuntime(long matchingId)
    {
        if (_activeRuntimes.TryGetValue(matchingId, out MatchRuntime? existing))
            return existing;
        if (Volatile.Read(ref _runtimeUseStarted))
            return _activeRuntimes.GetOrAdd(matchingId, CreateRuntime);

        lock (_runtimeCreationGate)
        {
            Volatile.Write(ref _runtimeUseStarted, true);
            return _activeRuntimes.GetOrAdd(matchingId, CreateRuntime);
        }
    }

    private MatchRuntime CreateRuntime(long matchingId) =>
        new(Volatile.Read(ref _store)?.GetOrCreate(matchingId));

    private sealed class MatchRuntimeOperation(
        MatchRuntimeRegistry owner,
        long matchingId,
        MatchRuntime runtime) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseOperation(matchingId, runtime);
        }
    }

    private sealed class MatchRuntime
    {
        private const int Active = 0;
        private const int Finalizing = 1;
        private const int Completed = 2;
        private readonly StoreMatchRuntime? _storeRuntime;
        private int _state = Active;
        private int _executionDepth;
        private long _nextFinalizationToken;
        private bool _initialized;
        private FinalizationWork? _currentFinalization;

        public MatchRuntime(StoreMatchRuntime? storeRuntime)
        {
            _storeRuntime = storeRuntime;
            SyncRoot = storeRuntime?.Sync ?? new object();
        }

        public object SyncRoot { get; }
        public bool IsTerminal => Volatile.Read(ref _state) != Active;

        public void EnsureInitialized(long matchingId, Action<long>? runtimeInitializer)
        {
            if (_initialized || runtimeInitializer == null)
                return;

            runtimeInitializer(matchingId);
            _initialized = true;
        }

        public bool TryBeginExecution()
        {
            if (Volatile.Read(ref _state) != Active)
                return false;

            _executionDepth++;
            return true;
        }

        public bool TryAttachFinalizationHooks(Action? beforeFinalized, Action? afterFinalized)
        {
            if (Volatile.Read(ref _state) != Finalizing || _currentFinalization == null)
                return false;

            _currentFinalization.AttachHooks(beforeFinalized, afterFinalized);
            return true;
        }

        public FinalizationClaim? EndExecution()
        {
            if (_executionDepth <= 0)
                throw new InvalidOperationException("A match execution cannot end more than once.");

            _executionDepth--;
            if (_executionDepth != 0 || Volatile.Read(ref _state) != Finalizing)
                return null;

            return ClaimCurrentFinalization();
        }

        public bool TryBeginFinalization(
            FinalizationWork finalization,
            out FinalizationClaim? claim)
        {
            claim = null;
            long token = GetNextFinalizationToken();
            finalization.BindToken(token);
            if (Interlocked.CompareExchange(ref _state, Finalizing, Active) != Active)
                return false;

            _currentFinalization = finalization;
            if (_executionDepth == 0)
                claim = ClaimCurrentFinalization();
            return true;
        }

        public void BeginCleanup(FinalizationClaim claim)
        {
            ValidateCurrentFinalization(claim);
            claim.Work.BeginCleanup(claim.Token);
        }

        public Action? Complete(FinalizationClaim claim)
        {
            ValidateCurrentFinalization(claim);
            Action? afterFinalized =
                claim.Work.CommitAndCreateAfterFinalizedAction(claim.Token);
            _currentFinalization = null;
            Volatile.Write(ref _state, Completed);
            if (_storeRuntime != null)
            {
                // 등록소가 정리를 끝냈으므로 저장소 런타임은 터미널이고 재정리 대상이 아니다.
                _storeRuntime.TryMarkTerminal();
                _storeRuntime.CleanupDone = true;
            }
            return afterFinalized;
        }

        public void ResetAfterFailedFinalization(FinalizationClaim claim)
        {
            ValidateCurrentFinalization(claim);
            claim.Work.ResetAfterFailedCleanup(claim.Token);
            _currentFinalization = null;
            Volatile.Write(ref _state, Active);
        }

        private FinalizationClaim ClaimCurrentFinalization()
        {
            if (Volatile.Read(ref _state) != Finalizing || _currentFinalization == null)
                throw new InvalidOperationException("No pending match finalization can be claimed.");

            return _currentFinalization.TryClaim()
                   ?? throw new InvalidOperationException("Match finalization already has a completion owner.");
        }

        private long GetNextFinalizationToken()
        {
            long token = unchecked(++_nextFinalizationToken);
            if (token == 0)
                token = unchecked(++_nextFinalizationToken);
            return token;
        }

        private void ValidateCurrentFinalization(FinalizationClaim claim)
        {
            if (Volatile.Read(ref _state) != Finalizing ||
                !ReferenceEquals(_currentFinalization, claim.Work) ||
                _currentFinalization.Token != claim.Token)
            {
                throw new InvalidOperationException("Finalization claim no longer owns this match runtime.");
            }
        }
    }
}
