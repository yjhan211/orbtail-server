using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class GameServerTickServiceTests
{
    [Fact]
    public async Task StartsTwoSharedTimersAndWaitsForBothToFinish()
    {
        var clock = new ManualTimers();
        var service = new GameServerTickService(NullLogger<GameServerTickService>.Instance, clock);
        int closureTicks = 0, matchTicks = 0;
        service.Start(() => closureTicks++, () => matchTicks++);
        Assert.Equal(2, clock.Timers.Count);
        var closure = clock.Timers[0];
        var match = clock.Timers[1];
        Assert.Equal(TimeSpan.FromSeconds(1), closure.Period);
        Assert.Equal(TimeSpan.FromMilliseconds(50), match.Period);
        Assert.Equal(closure.Period, closure.DueTime);
        Assert.Equal(match.Period, match.DueTime);
        closure.Fire();
        match.Fire();
        Assert.Equal(1, closureTicks);
        Assert.Equal(1, matchTicks);

        Task stopping = service.StopAsync();
        Assert.Same(stopping, service.StopAsync());
        Assert.True(closure.DisposalRequested);
        Assert.True(match.DisposalRequested);
        Assert.False(stopping.IsCompleted);
        closure.CompleteDisposal();
        Assert.False(stopping.IsCompleted);
        match.CompleteDisposal();
        await stopping;
        Assert.Throws<InvalidOperationException>(() => service.Start(() => { }, () => { }));
    }

    [Fact]
    public async Task StopBeforeStartIsSafeAndPreventsLateStart()
    {
        var clock = new ManualTimers();
        var service = new GameServerTickService(NullLogger<GameServerTickService>.Instance, clock);
        await service.StopAsync();
        Assert.Throws<InvalidOperationException>(() => service.Start(() => { }, () => { }));
        Assert.Empty(clock.Timers);
    }

    [Fact]
    public async Task PartialStartFailureStillDisposesTheFirstTimer()
    {
        var clock = new ManualTimers { FailSecondCreation = true };
        var service = new GameServerTickService(NullLogger<GameServerTickService>.Instance, clock);
        Assert.Throws<InvalidOperationException>(() => service.Start(() => { }, () => { }));
        Task stopping = service.StopAsync();
        var first = Assert.Single(clock.Timers);
        Assert.True(first.DisposalRequested);
        first.CompleteDisposal();
        await stopping;
    }

    [Fact]
    public async Task CallbackFailureDoesNotEscapeTimerOrBlockOtherTicks()
    {
        var clock = new ManualTimers();
        var service = new GameServerTickService(NullLogger<GameServerTickService>.Instance, clock);
        int count = 0;
        service.Start(() => throw new InvalidOperationException("tick failed"), () => count++);
        Assert.Null(Record.Exception(clock.Timers[0].Fire));
        clock.Timers[1].Fire();
        Assert.Equal(1, count);
        Task stopping = service.StopAsync();
        foreach (var timer in clock.Timers) timer.CompleteDisposal();
        await stopping;
    }

    private sealed class ManualTimers : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];
        public bool FailSecondCreation { get; init; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FailSecondCreation && Timers.Count == 1)
                throw new InvalidOperationException("timer creation failed");
            var timer = new ManualTimer(callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool DisposalRequested { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => CompleteDisposal();
        public ValueTask DisposeAsync()
        {
            DisposalRequested = true;
            return new ValueTask(_completion.Task);
        }
        public void CompleteDisposal() => _completion.TrySetResult();
    }
}
