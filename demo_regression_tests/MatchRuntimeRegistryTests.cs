using System.Reflection;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchRuntimeRegistryTests
{
    [Fact]
    public void TryFinalize_RunsCleanupExactlyOnceAndTombstonesMatchingId()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41001;
        int executionCount = 0;
        int cleanupCount = 0;

        Assert.True(registry.TryExecute(matchingId, () => executionCount++));
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => cleanupCount++));

        Assert.Equal(1, executionCount);
        Assert.Equal(1, cleanupCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(0, registry.ActiveCount);

        Assert.False(registry.TryExecute(matchingId, () => executionCount++));
        Assert.Null(registry.TryAcquireOperation(matchingId, () => executionCount++));
        Assert.False(registry.TryFinalize(
            matchingId,
            static () => true,
            () => cleanupCount++));

        Assert.Equal(1, executionCount);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task TryFinalize_ConcurrentCallers_RunCleanupExactlyOnce()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41004;
        const int callerCount = 8;
        using var ready = new CountdownEvent(callerCount);
        using var release = new ManualResetEventSlim(initialState: false);
        int cleanupCount = 0;

        Task<bool>[] attempts = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    ready.Signal();
                    release.Wait();
                    return registry.TryFinalize(
                        matchingId,
                        static () => true,
                        () => Interlocked.Increment(ref cleanupCount));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool allReady = ready.Wait(TimeSpan.FromSeconds(5));
        release.Set();
        Assert.True(allReady, "Concurrent finalizers did not reach the start gate in time.");

        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, cleanupCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public void TryFinalize_WithOutstandingOperation_DefersCleanupAndRejectsNewWork()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41002;
        var events = new List<string>();

        IDisposable? operation = registry.TryAcquireOperation(
            matchingId,
            () => events.Add("acquired"));

        Assert.NotNull(operation);
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => events.Add("cleanup")));
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(["acquired"], events);

        Assert.False(registry.TryExecute(matchingId, () => events.Add("late execution")));
        Assert.Null(registry.TryAcquireOperation(
            matchingId,
            () => events.Add("late operation")));
        Assert.Equal(["acquired"], events);

        operation!.Dispose();

        Assert.Equal(["acquired", "cleanup"], events);
        Assert.Equal(0, registry.ActiveCount);

        operation.Dispose();
        Assert.Equal(["acquired", "cleanup"], events);
    }

    [Fact]
    public void TryFinalize_WhenPredicateRejects_LeavesRuntimeActive()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41003;
        int executionCount = 0;
        int cleanupCount = 0;

        Assert.False(registry.TryFinalize(
            matchingId,
            static () => false,
            () => cleanupCount++));

        Assert.False(registry.IsTerminal(matchingId));
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(0, cleanupCount);
        Assert.True(registry.TryExecute(matchingId, () => executionCount++));
        Assert.Equal(1, executionCount);

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => cleanupCount++));
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(1, cleanupCount);
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public async Task TryExecute_SameMatchingId_SerializesActions()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41005;
        using var firstEntered = new ManualResetEventSlim(initialState: false);
        using var secondStarted = new ManualResetEventSlim(initialState: false);
        using var secondEntered = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        int active = 0;
        int peak = 0;

        Task<bool> first = Task.Factory.StartNew(
            () => registry.TryExecute(
                matchingId,
                () =>
                {
                    int current = Interlocked.Increment(ref active);
                    UpdateMaximum(ref peak, current);
                    firstEntered.Set();
                    try
                    {
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)), "First action did not enter in time.");

        Task<bool> second = Task.Factory.StartNew(
            () =>
            {
                secondStarted.Set();
                return registry.TryExecute(
                    matchingId,
                    () =>
                    {
                        int current = Interlocked.Increment(ref active);
                        UpdateMaximum(ref peak, current);
                        secondEntered.Set();
                        Interlocked.Decrement(ref active);
                    });
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(2)), "Second caller did not start in time.");
        bool enteredWhileFirstWasHeld = secondEntered.Wait(TimeSpan.FromMilliseconds(300));
        releaseFirst.Set();

        bool[] results = await Task.WhenAll(first, second);

        Assert.False(enteredWhileFirstWasHeld);
        Assert.All(results, Assert.True);
        Assert.True(secondEntered.IsSet);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task TryExecute_DifferentMatchingIds_CanOverlap()
    {
        var registry = new MatchRuntimeRegistry();
        using var bothEntered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(initialState: false);
        int active = 0;
        int peak = 0;

        Task<bool>[] executions = new[] { 41006L, 41007L }
            .Select(matchingId => Task.Factory.StartNew(
                () => registry.TryExecute(
                    matchingId,
                    () =>
                    {
                        int current = Interlocked.Increment(ref active);
                        UpdateMaximum(ref peak, current);
                        bothEntered.Signal();
                        try
                        {
                            release.Wait(TimeSpan.FromSeconds(5));
                        }
                        finally
                        {
                            Interlocked.Decrement(ref active);
                        }
                    }),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool overlapped = bothEntered.Wait(TimeSpan.FromSeconds(2));
        release.Set();
        bool[] results = await Task.WhenAll(executions);

        Assert.True(overlapped, "Different matching ids did not execute concurrently.");
        Assert.All(results, Assert.True);
        Assert.Equal(2, peak);
    }

    [Fact]
    public void TryFinalize_FromInsideTryExecute_DefersCleanupUntilActionReturns()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41008;
        var events = new List<string>();

        Assert.True(registry.TryExecute(
            matchingId,
            () =>
            {
                events.Add("execution-start");
                Assert.True(registry.TryFinalize(
                    matchingId,
                    static () => true,
                    () => events.Add("cleanup"),
                    () => events.Add("after-finalized")));
                events.Add("finalize-returned");
                Assert.True(registry.IsTerminal(matchingId));
                Assert.False(registry.TryExecute(
                    matchingId,
                    () => events.Add("late-execution")));
                events.Add("execution-end");
                Assert.DoesNotContain("cleanup", events);
                Assert.DoesNotContain("after-finalized", events);
            }));

        Assert.Equal(
            ["execution-start", "finalize-returned", "execution-end", "cleanup", "after-finalized"],
            events);
        Assert.Equal(0, registry.ActiveCount);
        Assert.True(registry.IsTerminal(matchingId));
    }

    [Fact]
    public async Task TryFinalize_CleanupBlocksOnlyTargetMatchingId()
    {
        var registry = new MatchRuntimeRegistry();
        const long finalizingMatchingId = 41009;
        const long siblingMatchingId = 41010;
        using var cleanupEntered = new ManualResetEventSlim(initialState: false);
        using var releaseCleanup = new ManualResetEventSlim(initialState: false);
        using var siblingEntered = new ManualResetEventSlim(initialState: false);

        Task<bool> finalization = Task.Factory.StartNew(
            () => registry.TryFinalize(
                finalizingMatchingId,
                static () => true,
                () =>
                {
                    cleanupEntered.Set();
                    releaseCleanup.Wait(TimeSpan.FromSeconds(5));
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(cleanupEntered.Wait(TimeSpan.FromSeconds(2)), "Cleanup did not start in time.");

        Task<bool> siblingExecution = Task.Factory.StartNew(
            () => registry.TryExecute(siblingMatchingId, siblingEntered.Set),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        bool siblingRanBeforeRelease = siblingEntered.Wait(TimeSpan.FromSeconds(2));
        releaseCleanup.Set();

        Assert.True(await finalization);
        Assert.True(await siblingExecution);
        Assert.True(siblingRanBeforeRelease, "A different match was blocked by target cleanup.");
        Assert.False(registry.TryExecute(finalizingMatchingId, static () => { }));
        Assert.Null(registry.TryAcquireOperation(finalizingMatchingId, static () => { }));
        Assert.False(registry.TryBindOwnerFence(finalizingMatchingId, ownerFence: 1));
    }

    [Fact]
    public async Task TryAcquireOperation_RegistrationAndFinalizePredicate_AreAtomic()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41011;
        using var registrationStarted = new ManualResetEventSlim(initialState: false);
        using var publishRegistration = new ManualResetEventSlim(initialState: false);
        using var finalizerStarted = new ManualResetEventSlim(initialState: false);
        int registeredSessions = 0;
        int cleanupCount = 0;

        Task<IDisposable?> acquisition = Task.Factory.StartNew(
            () => registry.TryAcquireOperation(
                matchingId,
                () =>
                {
                    registrationStarted.Set();
                    publishRegistration.Wait(TimeSpan.FromSeconds(5));
                    Volatile.Write(ref registeredSessions, 1);
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(
            registrationStarted.Wait(TimeSpan.FromSeconds(2)),
            "Registration did not acquire the runtime in time.");

        Task<bool> finalization = Task.Factory.StartNew(
            () =>
            {
                finalizerStarted.Set();
                return registry.TryFinalize(
                    matchingId,
                    () => Volatile.Read(ref registeredSessions) == 0,
                    () => Interlocked.Increment(ref cleanupCount));
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(finalizerStarted.Wait(TimeSpan.FromSeconds(2)), "Finalizer did not start in time.");
        publishRegistration.Set();

        IDisposable? operation = await acquisition;
        Assert.NotNull(operation);
        Assert.False(await finalization);
        Assert.Equal(0, cleanupCount);
        Assert.False(registry.IsTerminal(matchingId));

        operation!.Dispose();
        Volatile.Write(ref registeredSessions, 0);
        Assert.True(registry.TryFinalize(
            matchingId,
            () => Volatile.Read(ref registeredSessions) == 0,
            () => Interlocked.Increment(ref cleanupCount)));
        Assert.Equal(1, cleanupCount);
        Assert.True(registry.IsTerminal(matchingId));
    }

    [Fact]
    public void TryFinalize_WithMultipleOutstandingOperations_CleansUpAfterLastDispose()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41012;
        int cleanupCount = 0;

        IDisposable? first = registry.TryAcquireOperation(matchingId, static () => { });
        IDisposable? second = registry.TryAcquireOperation(matchingId, static () => { });
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => Interlocked.Increment(ref cleanupCount)));
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(0, cleanupCount);
        Assert.False(registry.TryExecute(matchingId, static () => { }));
        Assert.Null(registry.TryAcquireOperation(matchingId, static () => { }));
        Assert.False(registry.TryBindOwnerFence(matchingId, ownerFence: 1));

        first!.Dispose();
        Assert.Equal(0, cleanupCount);
        Assert.Equal(1, registry.ActiveCount);

        Parallel.Invoke(second!.Dispose, second.Dispose);

        Assert.Equal(1, cleanupCount);
        Assert.Equal(0, registry.ActiveCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.False(registry.TryExecute(matchingId, static () => { }));
        Assert.Null(registry.TryAcquireOperation(matchingId, static () => { }));
        Assert.False(registry.TryBindOwnerFence(matchingId, ownerFence: 1));
    }

    [Fact]
    public void TryFinalize_WhenCleanupThrows_ReactivatesRuntimeAndAllowsRetry()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41013;
        int cleanupAttempts = 0;
        int afterFinalizedCount = 0;

        Assert.Throws<InvalidOperationException>(() => registry.TryFinalize(
            matchingId,
            static () => true,
            () =>
            {
                Interlocked.Increment(ref cleanupAttempts);
                throw new InvalidOperationException("simulated cleanup failure");
            },
            () => Interlocked.Increment(ref afterFinalizedCount)));

        Assert.Equal(1, cleanupAttempts);
        Assert.Equal(0, afterFinalizedCount);
        Assert.False(registry.IsTerminal(matchingId));
        Assert.Equal(1, registry.ActiveCount);
        Assert.True(registry.TryExecute(matchingId, static () => { }));

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => Interlocked.Increment(ref cleanupAttempts),
            () => Interlocked.Increment(ref afterFinalizedCount)));
        Assert.Equal(2, cleanupAttempts);
        Assert.Equal(1, afterFinalizedCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public void TryFinalize_AfterFinalizedRunsOutsideMonitorExactlyOnce()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41014;
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        object syncRoot = GetRuntimeSyncRoot(registry, matchingId);
        int afterFinalizedCount = 0;

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            static () => { },
            () =>
            {
                Assert.False(Monitor.IsEntered(syncRoot));
                Interlocked.Increment(ref afterFinalizedCount);
            }));

        Assert.Equal(1, afterFinalizedCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.False(registry.TryFinalize(
            matchingId,
            static () => true,
            static () => { }));
        Assert.Equal(1, afterFinalizedCount);
    }

    [Fact]
    public void TryFinalize_WhenAfterFinalizedThrows_KeepsTerminalTombstone()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41015;
        int cleanupCount = 0;
        int afterFinalizedCount = 0;

        Assert.Throws<InvalidOperationException>(() => registry.TryFinalize(
            matchingId,
            static () => true,
            () => Interlocked.Increment(ref cleanupCount),
            () =>
            {
                Interlocked.Increment(ref afterFinalizedCount);
                throw new InvalidOperationException("simulated post-finalization failure");
            }));

        Assert.Equal(1, cleanupCount);
        Assert.Equal(1, afterFinalizedCount);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(0, registry.ActiveCount);
        Assert.False(registry.TryExecute(matchingId, static () => { }));
        Assert.False(registry.TryFinalize(
            matchingId,
            static () => true,
            () => Interlocked.Increment(ref cleanupCount)));
        Assert.Equal(1, cleanupCount);
        Assert.Equal(1, afterFinalizedCount);
    }

    [Fact]
    public void TryFinalize_WhenAnotherFinalizerIsPending_AttachesCallerHooksWithoutRepeatingCleanup()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41017;
        var events = new List<string>();
        int losingPredicateCount = 0;
        int losingCleanupCount = 0;
        IDisposable? operation = registry.TryAcquireOperation(matchingId, static () => { });
        Assert.NotNull(operation);
        object syncRoot = GetRuntimeSyncRoot(registry, matchingId);

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () =>
            {
                Assert.True(Monitor.IsEntered(syncRoot));
                events.Add("winner-before");
            },
            cleanup: () => events.Add("winner-cleanup"),
            afterFinalized: () =>
            {
                Assert.False(Monitor.IsEntered(syncRoot));
                events.Add("winner-post");
            }));
        Assert.False(registry.TryFinalize(
            matchingId,
            () =>
            {
                Interlocked.Increment(ref losingPredicateCount);
                return true;
            },
            beforeFinalized: () =>
            {
                Assert.True(Monitor.IsEntered(syncRoot));
                events.Add("loser-before");
            },
            cleanup: () => Interlocked.Increment(ref losingCleanupCount),
            afterFinalized: () =>
            {
                Assert.False(Monitor.IsEntered(syncRoot));
                events.Add("loser-post");
            }));

        Assert.Equal(0, losingPredicateCount);
        Assert.Equal(0, losingCleanupCount);
        Assert.Empty(events);

        operation!.Dispose();

        Assert.Equal(
            ["winner-before", "loser-before", "winner-cleanup", "winner-post", "loser-post"],
            events);
        Assert.Equal(0, losingPredicateCount);
        Assert.Equal(0, losingCleanupCount);
        Assert.True(registry.IsTerminal(matchingId));

        operation.Dispose();
        Assert.Equal(
            ["winner-before", "loser-before", "winner-cleanup", "winner-post", "loser-post"],
            events);
    }

    [Fact]
    public void TryFinalize_WhenCompletionWonTheRace_RunsOnlyCallerPostOutsideMonitor()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41018;
        int losingPredicateCount = 0;
        int losingBeforeCount = 0;
        int losingCleanupCount = 0;
        int postCount = 0;
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        object syncRoot = GetRuntimeSyncRoot(registry, matchingId);
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            static () => { }));

        Assert.False(registry.TryFinalize(
            matchingId,
            () =>
            {
                Interlocked.Increment(ref losingPredicateCount);
                return true;
            },
            beforeFinalized: () => Interlocked.Increment(ref losingBeforeCount),
            cleanup: () => Interlocked.Increment(ref losingCleanupCount),
            afterFinalized: () =>
            {
                Assert.False(Monitor.IsEntered(syncRoot));
                Interlocked.Increment(ref postCount);
            }));

        Assert.Equal(0, losingPredicateCount);
        Assert.Equal(0, losingBeforeCount);
        Assert.Equal(0, losingCleanupCount);
        Assert.Equal(1, postCount);
    }

    [Fact]
    public void TryFinalize_ReentrantFromBefore_DropsNestedBeforeAndCleanupButRunsNestedAfter()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41023;
        var events = new List<string>();

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () =>
            {
                events.Add("outer-before");
                Assert.False(registry.TryFinalize(
                    matchingId,
                    static () => true,
                    beforeFinalized: () => events.Add("nested-before"),
                    cleanup: () => events.Add("nested-cleanup"),
                    afterFinalized: () => events.Add("nested-after")));
                events.Add("outer-before-end");
            },
            cleanup: () => events.Add("outer-cleanup"),
            afterFinalized: () => events.Add("outer-after")));

        Assert.Equal(
            ["outer-before", "outer-before-end", "outer-cleanup", "outer-after", "nested-after"],
            events);
        Assert.True(registry.IsTerminal(matchingId));
    }

    [Fact]
    public void TryFinalize_BotOperationLeasesHoldTerminalHooksUntilLastDispose()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41024;
        var events = new List<string>();

        IDisposable? first = registry.TryAcquireOperation(
            matchingId,
            () => events.Add("bot-prepare-1"));
        IDisposable? second = registry.TryAcquireOperation(
            matchingId,
            () => events.Add("bot-prepare-2"));
        Assert.NotNull(first);
        Assert.NotNull(second);

        events.Add("bot-dispatch-start");
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Add("terminal-before"),
            cleanup: () => events.Add("component-cleanup"),
            afterFinalized: () => events.Add("summary-after")));
        events.Add("bot-dispatch-end");

        first!.Dispose();
        Assert.Equal(
            ["bot-prepare-1", "bot-prepare-2", "bot-dispatch-start", "bot-dispatch-end"],
            events);

        second!.Dispose();
        Assert.Equal(
            [
                "bot-prepare-1",
                "bot-prepare-2",
                "bot-dispatch-start",
                "bot-dispatch-end",
                "terminal-before",
                "component-cleanup",
                "summary-after"
            ],
            events);
        Assert.True(registry.IsTerminal(matchingId));

        Parallel.Invoke(first.Dispose, second.Dispose);
        Assert.Equal(7, events.Count);
    }

    [Fact]
    public void TryExecute_WhenActionAndPostFail_PreservesBothExceptions()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41019;

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            registry.TryExecute(
                matchingId,
                () =>
                {
                    Assert.True(registry.TryFinalize(
                        matchingId,
                        static () => true,
                        static () => { },
                        () => throw new ApplicationException("post failure")));
                    throw new InvalidOperationException("action failure");
                }));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Equal("action failure", failure.Message),
            failure => Assert.Equal("post failure", failure.Message));
        Assert.True(registry.IsTerminal(matchingId));
    }

    [Fact]
    public void TryAcquireOperation_WhenAcquisitionAndPostFail_PreservesBothExceptions()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41020;

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            registry.TryAcquireOperation(
                matchingId,
                () =>
                {
                    Assert.True(registry.TryFinalize(
                        matchingId,
                        static () => true,
                        static () => { },
                        () => throw new ApplicationException("post failure")));
                    throw new InvalidOperationException("acquisition failure");
                }));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Equal("acquisition failure", failure.Message),
            failure => Assert.Equal("post failure", failure.Message));
        Assert.True(registry.IsTerminal(matchingId));
    }

    [Fact]
    public void TryExecute_WhenActionAndDeferredCleanupFail_PreservesBothExceptions()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41021;
        int postCount = 0;

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            registry.TryExecute(
                matchingId,
                () =>
                {
                    Assert.True(registry.TryFinalize(
                        matchingId,
                        static () => true,
                        () => throw new ApplicationException("cleanup failure"),
                        () => Interlocked.Increment(ref postCount)));
                    throw new InvalidOperationException("action failure");
                }));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Equal("action failure", failure.Message),
            failure => Assert.Equal("cleanup failure", failure.Message));
        Assert.Equal(0, postCount);
        Assert.False(registry.IsTerminal(matchingId));
        Assert.True(registry.TryExecute(matchingId, static () => { }));
    }

    [Fact]
    public void TryAcquireOperation_WhenAcquisitionAndDeferredCleanupFail_PreservesBothExceptions()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41022;
        int postCount = 0;

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            registry.TryAcquireOperation(
                matchingId,
                () =>
                {
                    Assert.True(registry.TryFinalize(
                        matchingId,
                        static () => true,
                        () => throw new ApplicationException("cleanup failure"),
                        () => Interlocked.Increment(ref postCount)));
                    throw new InvalidOperationException("acquisition failure");
                }));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Equal("acquisition failure", failure.Message),
            failure => Assert.Equal("cleanup failure", failure.Message));
        Assert.Equal(0, postCount);
        Assert.False(registry.IsTerminal(matchingId));
        Assert.True(registry.TryExecute(matchingId, static () => { }));
    }

    [Fact]
    public void CleanupCoordinator_DeferredAfterFinalizedFailure_DoesNotUndoTerminalState()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41016;
        var events = new List<string>();
        var coordinator = new MatchRuntimeCleanupCoordinator(
            registry,
            [new MatchRuntimeCleanupStep("component", _ => events.Add("cleanup"))],
            NullLogger.Instance);
        IDisposable? operation = registry.TryAcquireOperation(matchingId, static () => { });
        Assert.NotNull(operation);

        Assert.True(coordinator.TryFinalize(
            matchingId,
            beforeFinalized: () => events.Add("before"),
            afterFinalized: () =>
            {
                events.Add("after-finalized");
                throw new InvalidOperationException("simulated post-finalization failure");
            }));
        Assert.Empty(events);

        operation!.Dispose();

        Assert.Equal(["before", "cleanup", "after-finalized"], events);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.Equal(0, registry.ActiveCount);
        operation.Dispose();
        Assert.Equal(["before", "cleanup", "after-finalized"], events);
    }

    [Fact]
    public void CleanupCoordinator_BeforeAndComponentFailures_DoNotUndoTerminalCommit()
    {
        var registry = new MatchRuntimeRegistry();
        const long matchingId = 41025;
        var events = new List<string>();
        var coordinator = new MatchRuntimeCleanupCoordinator(
            registry,
            [
                new MatchRuntimeCleanupStep("failing-component", _ =>
                {
                    events.Add("component-failed");
                    throw new InvalidOperationException("simulated component failure");
                }),
                new MatchRuntimeCleanupStep("remaining-component", _ => events.Add("component-completed"))
            ],
            NullLogger.Instance);

        Assert.True(coordinator.TryFinalize(
            matchingId,
            beforeFinalized: () =>
            {
                events.Add("terminal-before");
                throw new InvalidOperationException("simulated terminal publication failure");
            },
            afterFinalized: () => events.Add("summary-after")));

        Assert.Equal(
            ["terminal-before", "component-failed", "component-completed", "summary-after"],
            events);
        Assert.True(registry.IsTerminal(matchingId));
        Assert.False(registry.TryExecute(matchingId, () => events.Add("late-gameplay")));
        Assert.DoesNotContain("late-gameplay", events);
    }

    private static object GetRuntimeSyncRoot(MatchRuntimeRegistry registry, long matchingId)
    {
        FieldInfo runtimesField = typeof(MatchRuntimeRegistry).GetField(
            "_activeRuntimes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object runtimes = runtimesField.GetValue(registry)!;
        MethodInfo tryGetValue = runtimes.GetType().GetMethod("TryGetValue")!;
        object?[] arguments = [matchingId, null];

        Assert.True((bool)tryGetValue.Invoke(runtimes, arguments)!);
        object runtime = arguments[1]!;
        return runtime.GetType().GetProperty("SyncRoot")!.GetValue(runtime)!;
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            int previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }
}
