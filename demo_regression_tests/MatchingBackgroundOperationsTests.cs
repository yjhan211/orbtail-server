using user_server.matching;

namespace demo_regression_tests;

public sealed class MatchingBackgroundOperationsTests
{
    [Fact]
    public async Task CompletedAndFailedOperationsDoNotPreventDrain()
    {
        var logger = new RecordingLogger();
        using var background = new MatchingBackgroundOperations(logger);
        Assert.True(background.TryRun(() => Task.CompletedTask, "immediate"));
        Assert.True(background.TryRun(() => throw new InvalidOperationException("failure"), "failed"));
        background.Shutdown();
        await background.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "Matching background operation failed"));
    }

    [Fact]
    public async Task ShutdownCancelsAndRejectsNewWorkButDrainWaitsForExistingWork()
    {
        using var background = new MatchingBackgroundOperations(new RecordingLogger());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(background.TryRun(() => release.Task, "pending"));
        background.Shutdown();
        Assert.True(background.ShutdownToken.IsCancellationRequested);
        Assert.False(background.TryRun(() => throw new Exception("must not run"), "rejected"));
        Task drain = background.DrainAsync();
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
