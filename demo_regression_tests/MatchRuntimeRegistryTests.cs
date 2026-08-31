using game_server.services;

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
}
