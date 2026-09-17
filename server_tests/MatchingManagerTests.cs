using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using user_server.matching;

namespace server_tests;

public sealed class MatchingManagerTests
{
    [Fact]
    public async Task StopBeforeStart_PreventsRestart()
    {
        var manager = CreateManager(NullLogger.Instance);
        await manager.StopMatchingLoopAsync();
        Assert.Throws<ObjectDisposedException>(manager.Start);
        await manager.StopAsync();
    }

    [Fact]
    public async Task StopWhileWaiting_CompletesAndIsRepeatable()
    {
        var manager = CreateManager(NullLogger.Instance);
        manager.Start();
        Assert.Throws<InvalidOperationException>(manager.Start);
        await manager.StopMatchingLoopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Task first = manager.StopAsync();
        Assert.Same(first, manager.StopAsync());
        await first;
    }

    [Fact]
    public async Task StopDuringExecution_WaitsWithoutStartingAnotherPass()
    {
        using var logger = new BlockingLeaderLogger();
        var manager = CreateManager(logger);
        manager.Start();
        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 다음 tick이 와도 리더 확인 도중 다른 회차를 시작하지 않는다.
            await Task.Delay(1100);
            Assert.Equal(1, logger.Checks);
            Task stop = manager.StopMatchingLoopAsync();
            Assert.False(stop.IsCompleted);
            logger.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, logger.Checks);
        }
        finally
        {
            logger.Release.Set();
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static MatchingManager CreateManager(ILogger logger)
    {
        // 리더 획득을 실패시켜 큐 처리 없이 반복 실행과 종료만 확인한다.
        var redis = new InMemoryRedisOperations { StringError = new InvalidOperationException("test failure") };
        var leader = new MatchingLeaderLease(redis, "test-node", logger.For<MatchingLeaderLease>());
        return new MatchingManager(NullLogger<MatchingManager>.Instance, null!, null!, null!, null!, leader);
    }

    private sealed class BlockingLeaderLogger : ILogger, IDisposable
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public int Checks;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("leader lease check failed")) return;
            Interlocked.Increment(ref Checks);
            Entered.TrySetResult(true);
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test gate timed out.");
        }
        public void Dispose() => Release.Dispose();
    }
}
