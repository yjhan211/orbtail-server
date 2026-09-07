using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class GameServerTickServiceTests
{
    [Fact]
    public async Task CreatesOneLoopPerExistingOrNewMatchAndStopsAll()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(101);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        var firstTick = Signal();
        var secondTick = Signal();
        service.Start(runtime => (runtime.MatchingId == 101 ? firstTick : secondTick).TrySetResult());
        var second = store.GetOrCreate(102);
        Assert.Equal(2, clock.Timers.Count);
        Assert.Same(first, store.GetOrCreate(101));
        Assert.Equal(2, clock.Timers.Count);
        foreach (var timer in clock.Timers)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(50), timer.Period);
            timer.Fire();
        }
        await Task.WhenAll(firstTick.Task, secondTick.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = service.StopAsync();
        Assert.Same(stopping, service.StopAsync());
        await stopping;
        Assert.True(first.TickLoop!.Completion.IsCompleted);
        Assert.True(second.TickLoop!.Completion.IsCompleted);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Null(store.GetOrCreate(103).TickLoop);
    }

    [Fact]
    public async Task SlowMatchDoesNotBlockOtherMatchAndShutdownWaitsWithoutOverlap()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        var entered = Signal();
        var otherRan = Signal();
        using var release = new ManualResetEventSlim();
        int firstCalls = 0;
        service.Start(runtime =>
        {
            if (runtime.MatchingId != 201)
            {
                otherRan.TrySetResult();
                return;
            }
            Interlocked.Increment(ref firstCalls);
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var first = store.GetOrCreate(201);
        store.GetOrCreate(202);
        try
        {
            clock.Timers[0].Fire();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 10; i++) clock.Timers[0].Fire();
            clock.Timers[1].Fire();
            await otherRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref firstCalls));

            Task stopping = service.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, firstCalls);
            Assert.True(first.TickLoop!.Completion.IsCompleted);
        }
        finally
        {
            release.Set();
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task TerminalInsideTickStopsOwnLoopBeforeCleanupWithoutDeadlock()
    {
        var clock = new ManualTimers();
        bool workFinished = false;
        var cleaned = Signal();
        MatchRuntime? match = null;
        var store = new MatchRuntimeStore(NullLogger.Instance, cleanupSteps:
        [
            new("test", _ =>
            {
                Assert.True(workFinished);
                Assert.True(clock.Timers[0].Disposed);
                cleaned.TrySetResult();
            })
        ]);
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        int calls = 0;
        service.Start(runtime =>
        {
            using (store.Enter(runtime))
            {
                calls++;
                runtime.TryMarkTerminal();
                workFinished = true;
            }
        });
        match = store.GetOrCreate(301);
        try
        {
            clock.Timers[0].Fire();
            await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await match.TickLoop!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Timers[0].Fire();
            Assert.Equal(1, calls);
            Assert.Null(store.Get(301));
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task RemovalStopsWaitingLoop()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        int calls = 0;
        service.Start(_ => calls++);
        var match = store.GetOrCreate(401);
        Assert.True(store.Remove(401));
        await match.TickLoop!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Timers[0].Fire();
        Assert.Equal(0, calls);
        await service.StopAsync();
    }

    [Fact]
    public async Task TickFailureDoesNotEndTheLoop()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        var failed = Signal();
        var recovered = Signal();
        int calls = 0;
        service.Start(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failed.TrySetResult();
                throw new InvalidOperationException("tick failure");
            }
            recovered.TrySetResult();
        });
        store.GetOrCreate(501);
        try
        {
            clock.Timers[0].Fire();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Timers[0].Fire();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task StopBeforeStartPreventsRegistration()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        await service.StopAsync();
        Assert.Throws<InvalidOperationException>(() => service.Start(_ => { }));
        Assert.Null(store.GetOrCreate(601).TickLoop);
        Assert.Empty(clock.Timers);
    }

    [Fact]
    public async Task ConcurrentCreationAndShutdownLeaveNoRunningLoops()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        service.Start(_ => { });
        var creation = Enumerable.Range(1001, 100)
            .Select(id => Task.Run(() => store.GetOrCreate(id))).ToArray();
        Task stopping = Task.Run(async () => await service.StopAsync());
        var matches = await Task.WhenAll(creation);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        foreach (var match in matches)
            if (match.TickLoop is { } loop)
                Assert.True(loop.Completion.IsCompleted);
    }

    [Fact]
    public async Task FailedDuplicateStartStillAllowsExistingLoopToStop()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1101);
        var clock = new ManualTimers();
        var service = new GameServerTickService(store, NullLogger<GameServerTickService>.Instance, clock);
        service.Start(_ => { });
        Assert.Throws<InvalidOperationException>(() => service.Start(_ => { }));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(clock.Timers).Disposed);
        Assert.True(first.TickLoop!.Completion.IsCompleted);
        Assert.Null(store.GetOrCreate(1103).TickLoop);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class ManualTimers : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {

            var timer = new ManualTimer(callback, state, period);
            Timers.Add(timer);
            return timer;
        }
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan interval) => !Disposed;
        public void Dispose() => Volatile.Write(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
