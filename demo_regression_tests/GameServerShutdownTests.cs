using game_server.matches;
using game_server;
using Microsoft.Extensions.DependencyInjection;
using network.infrastructure.messaging;

namespace demo_regression_tests;

public sealed class GameServerShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStopsShareOneTaskEvenWhenNatsCloseFails(bool fail)
    {
        var nats = new BlockingCloseClient();
        using var provider = GameServerDependencyInjectionTests.CreateProvider(nats);
        var server = provider.GetRequiredService<GameServer>();
        Task first = server.StopAsync(CancellationToken.None);
        await nats.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Task[][] calls = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => new[] { server.StopAsync(new CancellationToken(true)) })));
            Assert.All(calls, call => Assert.Same(first, call[0]));
            Assert.False(first.IsCompleted);
            Assert.Equal(1, nats.CloseCount);
        }
        finally
        {
            if (fail) nats.Completion.TrySetException(new InvalidOperationException("close failed"));
            else nats.Completion.TrySetResult();
        }

        // NATS 정리 오류는 MatchingLifecycleService가 기록하고 종료를 계속한다.
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(first, server.StopAsync(CancellationToken.None));
        Assert.Equal(1, nats.CloseCount);
    }

    [Fact]
    public async Task StartupFailureAndHostStopWaitForTheSameCleanup()
    {
        var nats = new BlockingCloseClient();
        using var provider = GameServerDependencyInjectionTests.CreateProvider(nats);
        var server = provider.GetRequiredService<GameServer>();
        Task starting = server.StartAsync(new CancellationToken(true));
        await nats.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = server.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(starting.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(1, nats.CloseCount);
        }
        finally
        {
            nats.Completion.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(stopping, server.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShutdownWaitsForMatchLoopBeforeClosingNats()
    {
        var nats = new BlockingCloseClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var provider = GameServerDependencyInjectionTests.CreateProvider(nats, services =>
            services.AddSingleton<Func<MatchRuntime, TimeProvider, MatchTickLoop>>(sp => TestGameSessionServices.CreateTickLoopFactory(
                sp.GetRequiredService<MatchRuntimeStore>(), _ =>
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(10));
                })));
        var server = provider.GetRequiredService<GameServer>();
        var ticks = provider.GetRequiredService<game_server.matches.MatchTickService>();
        var matches = provider.GetRequiredService<game_server.matches.MatchRuntimeStore>();
        ticks.Start();
        matches.GetOrCreate(701);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stopping = server.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            Assert.False(nats.CloseStarted.Task.IsCompleted);
            release.Set();
            await nats.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            nats.Completion.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            nats.Completion.TrySetResult();
            await server.StopAsync(CancellationToken.None);
        }
    }

    private sealed class BlockingCloseClient : INatsClient
    {
        private int _closeCount;
        public int CloseCount => Volatile.Read(ref _closeCount);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _closeCount);
            CloseStarted.TrySetResult();
            return Completion.Task;
        }
        public void Close() { }
        public void Publish(string subject, byte[] message) { }
        public void Subscribe(string subject, Action<string, byte[]> messageHandler, string? queue = null) { }
        public Task<byte[]> RequestAsync(string subject, byte[] message, TimeSpan timeout,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SubscribeRequest(string subject, Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler,
            string? queue = null) => throw new NotSupportedException();
    }
}
