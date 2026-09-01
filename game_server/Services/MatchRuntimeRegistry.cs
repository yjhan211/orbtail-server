using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace game_server.services;

/// <summary>
///     Owns the serialization boundary and terminal lifecycle for each in-memory match.
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

    public bool TryExecute(long matchingId, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return false;

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        Action? afterFinalized = null;
        return ExecuteWithAfterFinalized(() =>
        {
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

                ExceptionDispatchInfo? actionFailure = null;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    actionFailure = ExceptionDispatchInfo.Capture(ex);
                }

                ExceptionDispatchInfo? finalizationFailure = null;
                try
                {
                    FinalizationWork? deferredFinalization = runtime.EndExecution();
                    if (deferredFinalization != null)
                    {
                        afterFinalized = CompleteFinalization(
                            matchingId,
                            runtime,
                            deferredFinalization);
                    }
                }
                catch (Exception ex)
                {
                    finalizationFailure = ExceptionDispatchInfo.Capture(ex);
                }

                ThrowIfFailures(
                    actionFailure,
                    finalizationFailure,
                    "Match action and deferred finalization both failed.");
                return true;
            }
        }, () => afterFinalized);
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
    ///     A losing caller may attach only its hooks to pending work; it never contributes another
    ///     component cleanup plan. Once the before phase has started or completion has won the race,
    ///     a late before hook is dropped while a late after hook retains the public post-commit contract.
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
            postFinalization = afterFinalized;
            return ExecuteWithAfterFinalized(static () => false, () => postFinalization);
        }

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        return ExecuteWithAfterFinalized(() =>
        {
            lock (runtime.SyncRoot)
            {
                if (_completedMatchingIds.ContainsKey(matchingId))
                {
                    _activeRuntimes.TryRemove(
                        new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                    postFinalization = afterFinalized;
                    return false;
                }

                runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
                // A caller that already captured terminal publication or post-commit work may
                // arrive after another execution or operation won the transition. Attach only
                // those hooks to pending work; its predicate and component cleanup stay skipped.
                if (runtime.TryAttachFinalizationHooks(beforeFinalized, afterFinalized))
                    return false;

                // Evaluate state-dependent predicates under the same lifecycle lock as the
                // Active -> Finalizing transition. Connection registration uses this lock too.
                if (!canFinalize())
                    return false;

                var finalization = new FinalizationWork(cleanup, beforeFinalized, afterFinalized);
                if (!runtime.TryBeginFinalization(finalization, out bool deferred))
                    return false;
                if (deferred)
                    return true;

                postFinalization = CompleteFinalization(matchingId, runtime, finalization);
                return true;
            }
        }, () => postFinalization);
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
    ///     Binds the in-memory runtime to the distributed owner fence carried by its first
    ///     admitted handoff. Later admissions must present the same fence.
    /// </summary>
    public bool TryBindOwnerFence(long matchingId, long ownerFence)
    {
        if (matchingId <= 0 || ownerFence < 0 || _completedMatchingIds.ContainsKey(matchingId))
            return false;

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        lock (runtime.SyncRoot)
        {
            if (_completedMatchingIds.ContainsKey(matchingId))
            {
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                return false;
            }

            runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
            return runtime.TryBindOwnerFence(ownerFence);
        }
    }

    /// <summary>
    ///     Acquires a terminal-lifecycle lease for asynchronous work. The lease does not hold
    ///     a monitor across await, but final cleanup is deferred until every acquired lease
    ///     and synchronous execution has completed.
    /// </summary>
    public IDisposable? TryAcquireOperation(long matchingId, Action onAcquired)
    {
        ArgumentNullException.ThrowIfNull(onAcquired);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return null;

        MatchRuntime runtime = GetOrCreateRuntime(matchingId);
        Action? afterFinalized = null;
        return ExecuteWithAfterFinalized(() =>
        {
            lock (runtime.SyncRoot)
            {
                if (_completedMatchingIds.ContainsKey(matchingId))
                {
                    _activeRuntimes.TryRemove(
                        new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                    return null;
                }

                runtime.EnsureInitialized(matchingId, Volatile.Read(ref _runtimeInitializer));
                if (!runtime.TryBeginExecution())
                    return null;

                ExceptionDispatchInfo? acquisitionFailure = null;
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
                    return new MatchRuntimeOperation(this, matchingId, runtime);

                ExceptionDispatchInfo? finalizationFailure = null;
                try
                {
                    FinalizationWork? deferredFinalization = runtime.EndExecution();
                    if (deferredFinalization != null)
                    {
                        afterFinalized = CompleteFinalization(
                            matchingId,
                            runtime,
                            deferredFinalization);
                    }
                }
                catch (Exception ex)
                {
                    finalizationFailure = ExceptionDispatchInfo.Capture(ex);
                }

                ThrowIfFailures(
                    acquisitionFailure,
                    finalizationFailure,
                    "Match operation acquisition and deferred finalization both failed.");
                return null;
            }
        }, () => afterFinalized);
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

    private Action? CompleteFinalization(
        long matchingId,
        MatchRuntime runtime,
        FinalizationWork finalization)
    {
        try
        {
            finalization.RunBeforeFinalized();
            finalization.ComponentCleanup();
            runtime.Complete();
            RememberCompleted(matchingId);
            _activeRuntimes.TryRemove(
                new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
            return finalization.CreateAfterFinalizedAction();
        }
        catch
        {
            runtime.ResetAfterFailedFinalization();
            throw;
        }
    }

    private void ReleaseOperation(long matchingId, MatchRuntime runtime)
    {
        Action? afterFinalized = null;
        ExecuteWithAfterFinalized(() =>
        {
            lock (runtime.SyncRoot)
            {
                FinalizationWork? deferredFinalization = runtime.EndExecution();
                if (deferredFinalization != null)
                {
                    afterFinalized = CompleteFinalization(
                        matchingId,
                        runtime,
                        deferredFinalization);
                }
            }
        }, () => afterFinalized);
    }

    private static TResult ExecuteWithAfterFinalized<TResult>(
        Func<TResult> operation,
        Func<Action?> getAfterFinalized)
    {
        TResult result = default!;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            result = operation();
        }
        catch (Exception ex)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(ex);
        }

        ExceptionDispatchInfo? postFailure = null;
        try
        {
            getAfterFinalized()?.Invoke();
        }
        catch (Exception ex)
        {
            postFailure = ExceptionDispatchInfo.Capture(ex);
        }

        if (primaryFailure != null && postFailure != null)
        {
            throw new AggregateException(
                "Match operation and post-finalization callback both failed.",
                primaryFailure.SourceException,
                postFailure.SourceException);
        }

        if (primaryFailure != null)
            primaryFailure.Throw();
        if (postFailure != null)
            postFailure.Throw();
        return result;
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

    private static void ExecuteWithAfterFinalized(
        Action operation,
        Func<Action?> getAfterFinalized)
    {
        ExecuteWithAfterFinalized(
            () =>
            {
                operation();
                return true;
            },
            getAfterFinalized);
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

    private sealed class FinalizationWork
    {
        // These collections and the phase flag are touched only while the owning
        // MatchRuntime.SyncRoot is held. The post-commit action closes over an immutable
        // array snapshot before the runtime is completed and the monitor is released.
        private readonly List<Action> _beforeFinalized = [];
        private readonly List<Action> _afterFinalized = [];
        private bool _beforeFinalizedStarted;

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

        public void AttachHooks(Action? beforeFinalized, Action? afterFinalized)
        {
            AttachBeforeFinalized(beforeFinalized);
            AttachAfterFinalized(afterFinalized);
        }

        public void RunBeforeFinalized()
        {
            _beforeFinalizedStarted = true;
            if (_beforeFinalized.Count == 0)
                return;

            Action[] callbacks = _beforeFinalized.ToArray();
            InvokeFinalizationCallbacks(
                callbacks,
                "Multiple pre-finalization callbacks failed.");
        }

        private void AttachBeforeFinalized(Action? beforeFinalized)
        {
            if (!_beforeFinalizedStarted && beforeFinalized != null)
                _beforeFinalized.Add(beforeFinalized);
        }

        public void AttachAfterFinalized(Action? afterFinalized)
        {
            if (afterFinalized != null)
                _afterFinalized.Add(afterFinalized);
        }

        public Action? CreateAfterFinalizedAction()
        {
            if (_afterFinalized.Count == 0)
                return null;

            Action[] callbacks = _afterFinalized.ToArray();
            return () => InvokeFinalizationCallbacks(
                callbacks,
                "Multiple post-finalization callbacks failed.");
        }
    }

    private MatchRuntime GetOrCreateRuntime(long matchingId)
    {
        if (_activeRuntimes.TryGetValue(matchingId, out MatchRuntime? existing))
            return existing;
        if (Volatile.Read(ref _runtimeUseStarted))
            return _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());

        lock (_runtimeCreationGate)
        {
            Volatile.Write(ref _runtimeUseStarted, true);
            return _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());
        }
    }

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
        private int _state = Active;
        private int _executionDepth;
        private long _ownerFence;
        private bool _initialized;
        private FinalizationWork? _currentFinalization;

        public object SyncRoot { get; } = new();
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

        public bool TryBindOwnerFence(long ownerFence)
        {
            if (Volatile.Read(ref _state) != Active)
                return false;
            if (ownerFence == 0)
                return _ownerFence == 0;
            if (_ownerFence == 0)
            {
                _ownerFence = ownerFence;
                return true;
            }

            return _ownerFence == ownerFence;
        }

        public bool TryAttachFinalizationHooks(Action? beforeFinalized, Action? afterFinalized)
        {
            if (Volatile.Read(ref _state) != Finalizing || _currentFinalization == null)
                return false;

            _currentFinalization.AttachHooks(beforeFinalized, afterFinalized);
            return true;
        }

        public FinalizationWork? EndExecution()
        {
            _executionDepth--;
            if (_executionDepth != 0 || Volatile.Read(ref _state) != Finalizing)
                return null;

            return _currentFinalization;
        }

        public bool TryBeginFinalization(FinalizationWork finalization, out bool deferred)
        {
            deferred = false;
            if (Interlocked.CompareExchange(ref _state, Finalizing, Active) != Active)
                return false;

            _currentFinalization = finalization;
            deferred = _executionDepth > 0;
            return true;
        }

        public void Complete()
        {
            _currentFinalization = null;
            Volatile.Write(ref _state, Completed);
        }

        public void ResetAfterFailedFinalization()
        {
            _currentFinalization = null;
            Interlocked.CompareExchange(ref _state, Active, Finalizing);
        }
    }
}
