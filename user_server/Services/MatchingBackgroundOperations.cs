using Microsoft.Extensions.Logging;

namespace user_server.services;

/// <summary>
///     매칭 서브시스템의 background 작업(입장 watchdog·lifecycle handler)을 추적하고 종료 토큰을 소유한다.
///     <see cref="MatchHandoffPublisher" />·<see cref="MatchmakingPass" />가 토큰을 보고 멈추며,
///     <see cref="MatchingManager" />가 종료 시 <see cref="Shutdown" /> → <see cref="DrainAsync" /> 순서로 배수한다.
///     프로세스에 하나. 작업 등록은 잠금 안에서 종료 플래그와 함께 판정한다.
/// </summary>
internal sealed class MatchingBackgroundOperations(ILogger logger) : IDisposable
{
    private readonly Dictionary<long, Task> _tasks = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private long _nextId;
    private int _stopping;

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    public bool TryRun(Func<Task> operation, string operationName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        lock (_gate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                logger.LogDebug(
                    "Ignoring matching background operation during shutdown: {OperationName}",
                    operationName);
                return false;
            }

            long id = Interlocked.Increment(ref _nextId);
            Task tracked = RunAsync(id, operation, operationName);
            if (!tracked.IsCompleted)
                _tasks[id] = tracked;
            return true;
        }
    }

    /// <summary>새 작업을 거부하고 진행 중인 작업에 취소를 알린다.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            Volatile.Write(ref _stopping, 1);
            _shutdownCts.Cancel();
        }
    }

    /// <summary>진행 중인 작업이 전부 끝날 때까지 기다린다. <see cref="Shutdown" /> 뒤에 부른다.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = _tasks.Values.ToArray();
            }

            if (pending.Length == 0)
                return;
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
            logger.LogWarning(ex, "Matching background operation failed: {OperationName}", operationName);
        }
        finally
        {
            lock (_gate)
            {
                _tasks.Remove(id);
            }
        }
    }
}
