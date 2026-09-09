using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     매치별 틱 루프의 시작과 종료를 관리한다.
///     새 매치가 생성되면 틱 루프를 연결하고, 실행이 끝난 루프는 추적 목록에서 제거한다.
///     서버 종료 시 새 루프 등록을 막고, 모든 루프를 중단한 뒤 진행 중인 처리가 끝날 때까지 기다린다.
///     실행 주기는 MatchTickLoop가, 실제 게임 처리는 MatchTickRunner가 담당한다.
/// </summary>
internal sealed class MatchTickService(
    MatchRuntimeStore matchRuntimes,
    MatchTickRunner tickRunner,
    ILogger<MatchTickService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<MatchRuntime, MatchTickLoop> _matchTickLoops = new();

    private readonly object _lifecycleLock = new();
    private bool _started;
    private Task? _stopTask;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started || _stopTask != null)
            {
                throw new InvalidOperationException("Game server ticks cannot be started again.");
            }
            _started = true;
            matchRuntimes.MatchCreated += StartMatchTickLoop;
        }

        foreach (long matchingId in matchRuntimes.ActiveIds())
        {
            if (matchRuntimes.GetOrNull(matchingId) is { } runtime)
            {
                StartMatchTickLoop(runtime);
            }
        }
        logger.LogInformation("Match tick loops started");
    }

    private void StartMatchTickLoop(MatchRuntime runtime)
    {
        lock (_lifecycleLock)
        {
            if (_stopTask != null || runtime.IsEnded || _matchTickLoops.ContainsKey(runtime) ||
                !ReferenceEquals(matchRuntimes.GetOrNull(runtime.MatchingId), runtime))
            {
                return;
            }

            var loop = new MatchTickLoop(runtime, tickRunner.Run, logger, _timeProvider);
            runtime.TickLoop = loop;

            if (runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(runtime.MatchingId), runtime))
            {
                loop.Stop();
                return;
            }

            _matchTickLoops.Add(runtime, loop);
            loop.Start();
            _ = ForgetCompletedLoopAsync(runtime, loop);
        }
    }

    private async Task ForgetCompletedLoopAsync(MatchRuntime runtime, MatchTickLoop loop)
    {
        try
        {
            await loop.Completion;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Match tick loop failed: MatchingId={MatchingId}", runtime.MatchingId);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _matchTickLoops.Remove(runtime);
            }
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        MatchTickLoop[] loops;
        lock (_lifecycleLock)
        {
            if (_stopTask != null)
            {
                return _stopTask;
            }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            loops = _matchTickLoops.Values.ToArray();
        }

        matchRuntimes.MatchCreated -= StartMatchTickLoop;
        _ = StopLoopsAsync(loops, completion);
        return completion.Task;
    }

    private static async Task StopLoopsAsync(MatchTickLoop[] loops, TaskCompletionSource completion)
    {
        try
        {
            foreach (var loop in loops)
            {
                loop.Stop();
            }
            await Task.WhenAll(loops.Select(loop => loop.Completion));
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }
}
