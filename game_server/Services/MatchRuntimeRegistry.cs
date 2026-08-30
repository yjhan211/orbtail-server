using System.Collections.Concurrent;

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
    private readonly object _globalExecutionLock = new();

    public int ActiveCount => _activeRuntimes.Count;

    public bool TryExecute(long matchingId, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return false;

        // Legacy swarm state still contains cross-match Dictionary/List/HashSet instances.
        // Serialize synchronous state application globally until those fields are moved into
        // a matchingId-owned runtime state object.
        lock (_globalExecutionLock)
        {
            var runtime = _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());
            lock (runtime.SyncRoot)
            {
                if (_completedMatchingIds.ContainsKey(matchingId))
                {
                    _activeRuntimes.TryRemove(
                        new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                    return false;
                }

                if (!runtime.TryBeginExecution())
                    return false;

                try
                {
                    action();
                    return true;
                }
                finally
                {
                    Action? deferredCleanup = runtime.EndExecution();
                    if (deferredCleanup != null)
                        CompleteFinalization(matchingId, runtime, deferredCleanup);
                }
            }
        }
    }

    public bool TryFinalize(long matchingId, Action cleanup)
    {
        return TryFinalize(matchingId, static () => true, cleanup);
    }

    public bool TryFinalize(long matchingId, Func<bool> canFinalize, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(canFinalize);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (matchingId <= 0 || _completedMatchingIds.ContainsKey(matchingId))
            return false;

        lock (_globalExecutionLock)
        {
            var runtime = _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());
            lock (runtime.SyncRoot)
            {
                if (_completedMatchingIds.ContainsKey(matchingId))
                {
                    _activeRuntimes.TryRemove(
                        new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                    return false;
                }

                // Evaluate state-dependent predicates under the same lifecycle lock as the
                // Active -> Finalizing transition. Connection registration uses this lock too.
                if (!canFinalize())
                    return false;

                if (!runtime.TryBeginFinalization(cleanup, out bool deferred))
                    return false;
                if (deferred)
                    return true;

                CompleteFinalization(matchingId, runtime, cleanup);
                return true;
            }
        }
    }

    public bool IsCompleted(long matchingId) =>
        matchingId > 0 && _completedMatchingIds.ContainsKey(matchingId);

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

        lock (_globalExecutionLock)
        {
            var runtime = _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());
            lock (runtime.SyncRoot)
            {
                if (_completedMatchingIds.ContainsKey(matchingId))
                {
                    _activeRuntimes.TryRemove(
                        new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                    return false;
                }

                return runtime.TryBindOwnerFence(ownerFence);
            }
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

        var runtime = _activeRuntimes.GetOrAdd(matchingId, static _ => new MatchRuntime());
        lock (runtime.SyncRoot)
        {
            if (_completedMatchingIds.ContainsKey(matchingId))
            {
                _activeRuntimes.TryRemove(
                    new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
                return null;
            }

            if (!runtime.TryBeginExecution())
                return null;

            try
            {
                // Connection registration can be supplied here so a no-human finalizer cannot
                // slip between acquiring the lifecycle lease and publishing the live session.
                onAcquired();
                return new MatchRuntimeOperation(this, matchingId, runtime);
            }
            catch
            {
                Action? deferredCleanup = runtime.EndExecution();
                if (deferredCleanup != null)
                    CompleteFinalization(matchingId, runtime, deferredCleanup);
                throw;
            }
        }
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

    private void CompleteFinalization(long matchingId, MatchRuntime runtime, Action cleanup)
    {
        try
        {
            cleanup();
            runtime.Complete();
            RememberCompleted(matchingId);
            _activeRuntimes.TryRemove(
                new KeyValuePair<long, MatchRuntime>(matchingId, runtime));
        }
        catch
        {
            runtime.ResetAfterFailedFinalization();
            throw;
        }
    }

    private void ReleaseOperation(long matchingId, MatchRuntime runtime)
    {
        lock (_globalExecutionLock)
        {
            lock (runtime.SyncRoot)
            {
                Action? deferredCleanup = runtime.EndExecution();
                if (deferredCleanup != null)
                    CompleteFinalization(matchingId, runtime, deferredCleanup);
            }
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
        private Action? _deferredCleanup;

        public object SyncRoot { get; } = new();
        public bool IsTerminal => Volatile.Read(ref _state) != Active;

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

        public Action? EndExecution()
        {
            _executionDepth--;
            if (_executionDepth != 0 || Volatile.Read(ref _state) != Finalizing)
                return null;

            Action? cleanup = _deferredCleanup;
            _deferredCleanup = null;
            return cleanup;
        }

        public bool TryBeginFinalization(Action cleanup, out bool deferred)
        {
            deferred = false;
            if (Interlocked.CompareExchange(ref _state, Finalizing, Active) != Active)
                return false;

            deferred = _executionDepth > 0;
            if (deferred)
                _deferredCleanup = cleanup;
            return true;
        }

        public void Complete()
        {
            Volatile.Write(ref _state, Completed);
        }

        public void ResetAfterFailedFinalization()
        {
            _deferredCleanup = null;
            Interlocked.CompareExchange(ref _state, Active, Finalizing);
        }
    }
}
