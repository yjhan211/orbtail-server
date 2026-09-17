using System.Reflection;
using game_server;
using network.routing;

namespace server_tests;

public sealed class GameServerNodeAdvertiserShutdownTests
{
    [Fact]
    public async Task StopWaitsForHeartbeatBeforeRemovingAndNeverPublishesAfterward()
    {
        var registry = new BlockingRegistry();
        var advertiser = Create(registry);
        await advertiser.StartAsync();
        Task heartbeat = InvokeHeartbeat(advertiser);
        await registry.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stopping = advertiser.StopAsync();
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.False(registry.Removed);
        }
        finally
        {
            registry.CompleteHeartbeat.TrySetResult();
            await heartbeat;
            await stopping;
        }

        Assert.True(registry.Removed);
        await InvokeHeartbeat(advertiser);
        Assert.Equal(2, registry.PublishCount);
        Assert.False(registry.PublishedAfterRemoval);
    }

    [Fact]
    public async Task FailedRemovalStillStopsHeartbeatsAndLogsFailure()
    {
        var registry = new BlockingRegistry { FailRemoval = true };
        var logger = new RecordingLogger();
        var advertiser = Create(registry, logger);
        await advertiser.StartAsync();

        await advertiser.StopAsync();
        await InvokeHeartbeat(advertiser);

        Assert.Equal(1, registry.PublishCount);
        Assert.True(logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "could not remove"));
    }

    private static GameServerNodeAdvertiser Create(BlockingRegistry registry, RecordingLogger? logger = null) =>
        new(registry, new GameServerNodeOptions { NodeId = "game-1", PublicHost = "localhost" },
            () => 0, logger ?? new RecordingLogger());

    // 실제 2초 타이머를 기다리지 않고 동일한 콜백을 실행해 Redis 응답 순서를 제어한다.
    private static Task InvokeHeartbeat(GameServerNodeAdvertiser advertiser) =>
        (Task)typeof(GameServerNodeAdvertiser)
            .GetMethod("HeartbeatAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(advertiser, null)!;

    private sealed class BlockingRegistry : IGameServerRegistry
    {
        public TaskCompletionSource HeartbeatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PublishCount { get; private set; }
        public bool Removed { get; private set; }
        public bool PublishedAfterRemoval { get; private set; }
        public bool FailRemoval { get; init; }

        public async Task PublishAsync(GameServerNodeDescriptor descriptor)
        {
            PublishCount++;
            if (PublishCount > 1)
            {
                HeartbeatStarted.TrySetResult();
                await CompleteHeartbeat.Task;
            }
            PublishedAfterRemoval |= Removed;
        }

        public Task RemoveAsync(string nodeId)
        {
            if (FailRemoval)
                throw new InvalidOperationException("Redis unavailable");
            Removed = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverAsync() =>
            Task.FromResult<IReadOnlyList<GameServerNodeDescriptor>>(Array.Empty<GameServerNodeDescriptor>());
    }
}
