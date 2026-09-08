using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     매치 생성에 맞춰 독립적인 틱 루프를 시작하고, 서버 종료 시 모든 루프의 완료를 기다린다.
///     공용 매치 순회 타이머는 없다. 게임 상태는 각 루프가 호출하는 처리기가 매치 잠금 안에서 다룬다.
/// </summary>
internal sealed class GameServerTickService(
    MatchRuntimeStore matchRuntimes,
    ILogger<GameServerTickService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _lifecycleLock = new();
    private readonly Dictionary<MatchRuntime, MatchTickLoop> _loops = new();
    private Action<MatchRuntime>? _tick;
    private Task? _stopTask;

    public void Start(Action<MatchRuntime> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        lock (_lifecycleLock)
        {
            if (_tick != null || _stopTask != null)
                throw new InvalidOperationException("Game server ticks cannot be started again.");
            _tick = tick;
            matchRuntimes.MatchCreated += StartMatchTickLoop;
        }

        // 구독 직전 만들어진 매치도 포함한다. 구독과 목록 조회 양쪽에 잡힌 매치는 한 번만 시작한다.
        foreach (long matchingId in matchRuntimes.ActiveIds())
        {
            if (matchRuntimes.GetOrNull(matchingId) is { } runtime)
                StartMatchTickLoop(runtime);
        }
        logger.LogInformation("Per-match tick loops started: IntervalMs=50");
    }

    private void StartMatchTickLoop(MatchRuntime runtime)
    {
        // 생성·제거와 루프 연결이 엇갈리지 않게 매치 잠금을 먼저 잡는다.
        lock (runtime.Sync)
        lock (_lifecycleLock)
        {
            if (_stopTask != null || runtime.IsEnded || _loops.ContainsKey(runtime) ||
                !ReferenceEquals(matchRuntimes.GetOrNull(runtime.MatchingId), runtime))
                return;

            var loop = new MatchTickLoop(runtime, _tick!, logger, _timeProvider);
            runtime.TickLoop = loop;
            _loops.Add(runtime, loop);
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
                _loops.Remove(runtime);
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            // _stopTask를 먼저 확정해 새 매치의 등록을 막는다.
            if (_stopTask != null)
                return _stopTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            matchRuntimes.MatchCreated -= StartMatchTickLoop;
            _ = StopLoopsAsync(_loops.Values.ToArray(), completion);
            return _stopTask;
        }
    }

    private static async Task StopLoopsAsync(MatchTickLoop[] loops, TaskCompletionSource completion)
    {
        try
        {
            foreach (var loop in loops)
                loop.Stop();
            await Task.WhenAll(loops.Select(loop => loop.Completion));
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }
}
