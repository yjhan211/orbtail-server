using game_server.services;

namespace demo_regression_tests;

public sealed class SwarmBotTickCoordinatorTests
{
    [Fact]
    public async Task SlowMatch_DoesNotBlockDifferentMatch()
    {
        var coordinator = new SwarmBotTickCoordinator();
        Assert.True(coordinator.RegisterMatching(101));
        Assert.True(coordinator.RegisterMatching(202));
        var slowEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task slow = Task.Run(async () =>
        {
            Assert.True(coordinator.TryBegin(101, true, out var lease));
            slowEntered.SetResult();
            await releaseSlow.Task;
            coordinator.Retire(lease!);
        });

        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Task fast = Task.Run(() =>
            {
                Assert.True(coordinator.TryBegin(202, true, out var lease));
                coordinator.Retire(lease!);
            });
            await fast.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(slow.IsCompleted);
            Assert.True(coordinator.GetDiagnostics(101).IsRunning);
            Assert.False(coordinator.GetDiagnostics(202).IsRunning);
        }
        finally
        {
            releaseSlow.TrySetResult();
            await slow;
        }
    }

    [Fact]
    public void BusyPulse_IsDroppedWithoutCatchUpAndOnlyEligiblePulseIsCounted()
    {
        var coordinator = new SwarmBotTickCoordinator();
        Assert.True(coordinator.RegisterMatching(303));
        Assert.True(coordinator.TryBegin(303, true, out var first));
        Assert.False(coordinator.TryBegin(303, true, out _));
        Assert.False(coordinator.TryBegin(303, false, out _));
        SwarmBotTickDiagnostics busy = coordinator.GetDiagnostics(303);
        Assert.True(busy.IsRunning);
        Assert.Equal(1, busy.BusySkips);
        Assert.Equal(1, busy.ConsecutiveBusySkips);
        coordinator.Retire(first!);
        SwarmBotTickDiagnostics retired = coordinator.GetDiagnostics(303);
        Assert.False(retired.IsRunning);
        Assert.Equal(0, retired.ConsecutiveBusySkips);
        Assert.True(coordinator.TryRecordBusySkip(303));
        SwarmBotTickDiagnostics contended = coordinator.GetDiagnostics(303);
        Assert.Equal(2, contended.BusySkips);
        Assert.Equal(1, contended.ConsecutiveBusySkips);
        Assert.True(coordinator.TryBegin(303, true, out var explicitNextPulse));
        coordinator.Retire(explicitNextPulse!);
    }

    [Fact]
    public void ClearMatching_DetachedRetireNeverReaddsState()
    {
        var coordinator = new SwarmBotTickCoordinator();
        Assert.True(coordinator.RegisterMatching(404));
        Assert.True(coordinator.TryBegin(404, true, out var lease));
        coordinator.ClearMatching(404);
        coordinator.Retire(lease!);
        Assert.False(coordinator.GetDiagnostics(404).IsRegistered);
        Assert.False(coordinator.TryBegin(404, true, out _));
    }

    [Fact]
    public void OutstandingRuntimeOperation_DefersCleanupAndDetachedRetireCannotReadd()
    {
        const long matchingId = 454;
        var coordinator = new SwarmBotTickCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(
            id => Assert.True(coordinator.RegisterMatching(id)));
        SwarmBotTickCoordinator.SwarmBotTickLease? tickLease = null;
        IDisposable? outerOperation = registry.TryAcquireOperation(
            matchingId,
            () => Assert.True(
                coordinator.TryBegin(matchingId, true, out tickLease)));

        Assert.NotNull(outerOperation);
        Assert.NotNull(tickLease);
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => coordinator.ClearMatching(matchingId)));
        Assert.True(registry.IsTerminal(matchingId));
        Assert.True(coordinator.GetDiagnostics(matchingId).IsRegistered);
        Assert.False(registry.TryExecute(matchingId, static () => { }));

        outerOperation.Dispose();
        Assert.False(coordinator.GetDiagnostics(matchingId).IsRegistered);
        coordinator.Retire(tickLease!);
        Assert.False(coordinator.GetDiagnostics(matchingId).IsRegistered);
        Assert.False(coordinator.TryBegin(matchingId, true, out _));
    }

    [Fact]
    public async Task ClaimBeforeQueuedWorker_DropsOverlapWithoutAutomaticRerun()
    {
        const long matchingId = 484;
        var coordinator = new SwarmBotTickCoordinator();
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.True(coordinator.TryBegin(matchingId, true, out var claimedLease));
        var releaseWorker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        Task worker = Task.Run(async () =>
        {
            await releaseWorker.Task;
            Interlocked.Increment(ref executionCount);
            coordinator.Retire(claimedLease!);
        });

        Assert.False(coordinator.TryBegin(matchingId, true, out _));
        releaseWorker.SetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, executionCount);
        Assert.False(coordinator.GetDiagnostics(matchingId).IsRunning);

        Assert.True(coordinator.TryBegin(matchingId, true, out var nextPulse));
        Interlocked.Increment(ref executionCount);
        coordinator.Retire(nextPulse!);
        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task NonBlockingRuntimeClaim_DropsSlowMatchAndAllowsSibling()
    {
        const long slowMatchingId = 494;
        const long siblingMatchingId = 495;
        var coordinator = new SwarmBotTickCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(
            id => Assert.True(coordinator.RegisterMatching(id)));
        SwarmBotTickCoordinator.SwarmBotTickLease? slowLease = null;
        IDisposable? slowOperation = registry.TryAcquireOperation(
            slowMatchingId,
            () => Assert.True(
                coordinator.TryBegin(slowMatchingId, true, out slowLease)));
        Assert.NotNull(slowOperation);
        Assert.NotNull(slowLease);

        var slowMonitorEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSlowMonitor = new ManualResetEventSlim(false);
        Task<bool> slowExecution = Task.Factory.StartNew(
            () => registry.TryExecute(
                slowMatchingId,
                () =>
                {
                    slowMonitorEntered.SetResult();
                    releaseSlowMonitor.Wait();
                }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await slowMonitorEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        SwarmBotTickCoordinator.SwarmBotTickLease? rejectedLease = null;
        IDisposable? rejectedOperation = null;
        IDisposable? siblingOperation = null;
        SwarmBotTickCoordinator.SwarmBotTickLease? siblingLease = null;
        try
        {
            rejectedOperation = registry.TryAcquireOperationIfAvailable(
                slowMatchingId,
                () => coordinator.TryBegin(
                    slowMatchingId,
                    true,
                    out rejectedLease));
            Assert.Null(rejectedOperation);
            Assert.Null(rejectedLease);
            Assert.True(coordinator.TryRecordBusySkip(slowMatchingId));

            siblingOperation = registry.TryAcquireOperationIfAvailable(
                siblingMatchingId,
                () => Assert.True(
                    coordinator.TryBegin(
                        siblingMatchingId,
                        true,
                        out siblingLease)));
            Assert.NotNull(siblingOperation);
            Assert.NotNull(siblingLease);
            Assert.Equal(
                1,
                coordinator.GetDiagnostics(slowMatchingId).BusySkips);
        }
        finally
        {
            siblingOperation?.Dispose();
            if (siblingLease != null)
                coordinator.Retire(siblingLease);
            releaseSlowMonitor.Set();
            Assert.True(await slowExecution.WaitAsync(TimeSpan.FromSeconds(1)));
            slowOperation!.Dispose();
            coordinator.Retire(slowLease!);
        }
    }

    [Fact]
    public async Task MetricsWindows_AreIsolatedPerMatchDuringConcurrentRecording()
    {
        var coordinator = new SwarmBotTickCoordinator();
        Assert.True(coordinator.RegisterMatching(505));
        Assert.True(coordinator.RegisterMatching(606));
        Assert.True(coordinator.TryBegin(505, true, out var firstLease));
        Assert.False(coordinator.TryBegin(505, true, out _));
        coordinator.Retire(firstLease!);
        SwarmBotTickMetricsBatch? first = null;
        SwarmBotTickMetricsBatch? second = null;
        await Task.WhenAll(
            Task.Run(() => first = RecordWindow(coordinator, 505, 1d)),
            Task.Run(() => second = RecordWindow(coordinator, 606, 2d)));
        SwarmBotTickMetricsBatch firstBatch = Assert.IsType<SwarmBotTickMetricsBatch>(first);
        SwarmBotTickMetricsBatch secondBatch = Assert.IsType<SwarmBotTickMetricsBatch>(second);
        Assert.Equal(505, firstBatch.MatchingId);
        Assert.Equal(606, secondBatch.MatchingId);
        Assert.Equal(200, firstBatch.TickSamples.Length);
        Assert.Equal(200, secondBatch.TickSamples.Length);
        Assert.All(firstBatch.TickSamples, value => Assert.Equal(1d, value));
        Assert.All(secondBatch.TickSamples, value => Assert.Equal(2d, value));
        Assert.Equal(1, firstBatch.BusySkips);
        Assert.Equal(1, firstBatch.MaxConsecutiveBusySkips);
        Assert.Equal(0, secondBatch.BusySkips);
        Assert.Equal(0, secondBatch.MaxConsecutiveBusySkips);
    }

    [Fact]
    public void ForeignLease_IsRejectedWithoutChangingOwnerState()
    {
        var owner = new SwarmBotTickCoordinator();
        var foreign = new SwarmBotTickCoordinator();
        Assert.True(owner.RegisterMatching(707));
        Assert.True(owner.TryBegin(707, true, out var lease));
        Assert.Throws<InvalidOperationException>(() => foreign.Record(
            lease!,
            new SwarmBotTickSample(1d, 0d, 0d, 0d, 0d)));
        Assert.Throws<InvalidOperationException>(() => foreign.Retire(lease!));
        owner.Retire(lease!);
    }

    private static SwarmBotTickMetricsBatch? RecordWindow(
        SwarmBotTickCoordinator coordinator,
        long matchingId,
        double elapsed)
    {
        SwarmBotTickMetricsBatch? batch = null;
        for (int sample = 0; sample < 200; sample++)
        {
            Assert.True(coordinator.TryBegin(matchingId, true, out var lease));
            batch = coordinator.Record(
                lease!,
                new SwarmBotTickSample(elapsed, elapsed, elapsed, elapsed, elapsed));
            coordinator.Retire(lease!);
        }

        return batch;
    }
}
