using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchTickServiceTests
{
    [Fact]
    public async Task CreatesOneLoopPerExistingOrNewMatchAndStopsAll()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(101);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        var firstTick = Signal();
        var secondTick = Signal();
        processMatchTick = runtime => (runtime.MatchingId == 101 ? firstTick : secondTick).TrySetResult();
        service.Start();
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        var entered = Signal();
        var otherRan = Signal();
        using var release = new ManualResetEventSlim();
        int firstCalls = 0;
        processMatchTick = runtime =>
        {
            if (runtime.MatchingId != 201)
            {
                otherRan.TrySetResult();
                return;
            }
            Interlocked.Increment(ref firstCalls);
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        service.Start();
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance, onRedisCleanup: _ =>
        {
            Assert.True(workFinished);
            Assert.True(clock.Timers[0].Disposed);
            cleaned.TrySetResult();
        });
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        int calls = 0;
        processMatchTick = runtime =>
        {
            using (MatchRuntimeStore.Enter(runtime))
            {
                calls++;
                runtime.TryMarkEnded();
                workFinished = true;
            }
        };
        service.Start();
        match = store.GetOrCreate(301);
        try
        {
            clock.Timers[0].Fire();
            await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await match.TickLoop!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Timers[0].Fire();
            Assert.Equal(1, calls);
            Assert.Null(store.GetOrNull(301));
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task RemovalStopsWaitingLoop()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        int calls = 0;
        processMatchTick = _ => calls++;
        service.Start();
        var match = store.GetOrCreate(401);
        Assert.True(store.Remove(401));
        Assert.True(match.IsEnded);
        Assert.Null(store.GetOrNull(401));
        Assert.False(store.Remove(401));
        await match.TickLoop!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Timers[0].Fire();
        Assert.Equal(0, calls);
        await service.StopAsync();
    }

    [Fact]
    public async Task TickFailureDoesNotEndTheLoop()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        var failed = Signal();
        var recovered = Signal();
        int calls = 0;
        processMatchTick = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failed.TrySetResult();
                throw new InvalidOperationException("tick failure");
            }
            recovered.TrySetResult();
        };
        service.Start();
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        await service.StopAsync();
        Assert.Throws<InvalidOperationException>(() => service.Start());
        Assert.Null(store.GetOrCreate(601).TickLoop);
        Assert.Empty(clock.Timers);
    }

    [Fact]
    public async Task ConcurrentCreationAndShutdownLeaveNoRunningLoops()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        processMatchTick = _ => { };
        service.Start();
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1101);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        processMatchTick = _ => { };
        service.Start();
        Assert.Throws<InvalidOperationException>(() => service.Start());
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(clock.Timers).Disposed);
        Assert.True(first.TickLoop!.Completion.IsCompleted);
        Assert.Null(store.GetOrCreate(1103).TickLoop);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task MatchEndingBeforeLoopAttachmentDisposesUnstartedLoop()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(1301);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        var timerCreated = Signal();
        using var allowAttachment = new ManualResetEventSlim();
        clock.OnCreated = _ =>
        {
            timerCreated.TrySetResult();
            if (!allowAttachment.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Loop attachment was not released.");
        };
        int ticks = 0;
        processMatchTick = _ => Interlocked.Increment(ref ticks);
        Task starting = Task.Run(() => service.Start());
        try
        {
            await timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(runtime.TickLoop);
            // 기존 매치 잠금이 잡혀 있었다면 이 제거는 시작 작업을 기다리게 된다.
            Assert.True(await Task.Run(() => store.Remove(1301)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(runtime.IsEnded);
            allowAttachment.Set();
            await starting.WaitAsync(TimeSpan.FromSeconds(5));

            var timer = Assert.Single(clock.Timers);
            Assert.True(timer.Disposed);
            Assert.NotNull(runtime.TickLoop);
            Assert.True(runtime.TickLoop.Completion.IsCompleted);
            timer.Fire();
            Assert.Equal(0, Volatile.Read(ref ticks));
        }
        finally
        {
            allowAttachment.Set();
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MatchEndingAfterLoopAttachmentBeforeStartDoesNotRunTick()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(1302);
        var clock = new ManualTimers();
        int ticks = 0;
        var loop = new MatchTickLoop(runtime, _ => Interlocked.Increment(ref ticks), NullLogger.Instance, clock);
        runtime.TickLoop = loop;

        Assert.True(store.Remove(1302));
        Assert.True(Assert.Single(clock.Timers).Disposed);
        loop.Start();
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Timers[0].Fire();
        Assert.Equal(0, Volatile.Read(ref ticks));
    }

    [Fact]
    public async Task StopDisposesTimersOutsideLifecycleLockAndReusesStopTask()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var clock = new ManualTimers();
        Action<MatchRuntime> processMatchTick = _ => { };
        var service = new MatchTickService(store,
            TestGameSessionServices.CreateTickRunner(store, runtime => processMatchTick(runtime)),
            NullLogger<MatchTickService>.Instance, clock);
        processMatchTick = _ => { };
        service.Start();
        store.GetOrCreate(1201);
        var lifecycleLock = typeof(MatchTickService).GetField(
            "_lifecycleLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        bool disposedUnderLock = true;
        Task? repeatedStop = null;
        Assert.Single(clock.Timers).OnDispose = () =>
        {
            disposedUnderLock = Monitor.IsEntered(lifecycleLock);
            repeatedStop = service.StopAsync();
        };

        Task stopping = service.StopAsync();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(disposedUnderLock);
        Assert.Same(stopping, repeatedStop);
        Assert.Same(stopping, service.StopAsync());
        Assert.Null(store.GetOrCreate(1202).TickLoop);
    }

    internal sealed class ManualTimers : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];
        public Action<ManualTimer>? OnCreated { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {

            var timer = new ManualTimer(callback, state, period);
            Timers.Add(timer);
            OnCreated?.Invoke(timer);
            return timer;
        }
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; } = period;
        private int _disposed;
        public Action? OnDispose { get; set; }
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan interval) => !Disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                OnDispose?.Invoke();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
