using Microsoft.Extensions.Logging;

namespace network.infrastructure;

/// <summary>
///     비동기 작업을 추적한다.
///     전달받은 작업을 실행하고, 완료되면 추적 목록에서 제거한다.
///     종료 시 새 작업을 거부하고 진행 중인 작업에 취소를 요청한다.
///     작업이 모두 끝났는지 기다리는 기능을 제공하며, 작업 중 발생한 예외는 로그로 남긴다.
/// </summary>
public sealed class BackgroundTaskTracker(ILogger logger) : IDisposable
{
    private readonly Dictionary<long, Task> _tasks = new();
    private readonly object _taskLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private long _nextId;
    private int _stopping;

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    public bool TryRun(Func<Task> operation, string operationName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        lock (_taskLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                logger.LogDebug("Ignoring background operation during shutdown: {OperationName}", operationName);
                return false;
            }

            long id = Interlocked.Increment(ref _nextId);
            var tracked = RunAsync(id, operation, operationName);
            if (!tracked.IsCompleted)
            {
                _tasks[id] = tracked;
            }
            return true;
        }
    }

    public void Shutdown()
    {
        lock (_taskLock)
        {
            Volatile.Write(ref _stopping, 1);
            _shutdownCts.Cancel();
        }
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_taskLock)
            {
                pending = _tasks.Values.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }
            await Task.WhenAll(pending);
        }
    }

    public void Dispose()
    {
        _shutdownCts.Dispose();
    }

    private async Task RunAsync(long id, Func<Task> operation, string operationName)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background operation failed: {OperationName}", operationName);
        }
        finally
        {
            lock (_taskLock)
            {
                _tasks.Remove(id);
            }
        }
    }
}
