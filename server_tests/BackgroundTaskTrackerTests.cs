using network.infrastructure;
using user_server.matching;

namespace server_tests;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task CompletedAndFailedOperationsDoNotPreventDrain()
    {
        var logger = new RecordingLogger();
        using var taskTracker = new BackgroundTaskTracker(logger);
        Assert.True(taskTracker.TryRun(() => Task.CompletedTask, "immediate"));
        Assert.True(taskTracker.TryRun(() => throw new InvalidOperationException("failure"), "failed"));
        taskTracker.Shutdown();
        await taskTracker.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Background operation failed"));
    }

    [Fact]
    public async Task ShutdownCancelsAndRejectsNewWorkButDrainWaitsForExistingWork()
    {
        using var taskTracker = new BackgroundTaskTracker(new RecordingLogger());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(taskTracker.TryRun(() => release.Task, "pending"));
        taskTracker.Shutdown();
        Assert.True(taskTracker.ShutdownToken.IsCancellationRequested);
        Assert.False(taskTracker.TryRun(() => throw new Exception("must not run"), "rejected"));
        Task drain = taskTracker.DrainAsync();
        try
        {
            Assert.False(drain.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
