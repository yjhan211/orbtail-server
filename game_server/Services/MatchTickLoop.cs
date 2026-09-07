using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     매치 하나의 50ms 순차 루프. 이전 처리가 끝나야 다음 틱을 받으며 밀린 틱은 합쳐진다.
///     Stop은 다음 틱을 막고, Completion은 실행 중인 처리까지 끝났음을 나타낸다.
/// </summary>
internal sealed class MatchTickLoop
{
    private readonly PeriodicTimer _timer;
    private readonly MatchRuntime _runtime;
    private readonly Action<MatchRuntime> _tick;
    private readonly ILogger _logger;
    private int _stopping;

    public MatchTickLoop(MatchRuntime runtime, Action<MatchRuntime> tick, ILogger logger, TimeProvider timeProvider)
    {
        _runtime = runtime;
        _tick = tick;
        _logger = logger;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), timeProvider);
    }

    public Task Completion { get; private set; } = Task.CompletedTask;

    // 소유자가 등록을 마친 뒤 한 번 호출한다. 매치 생성 잠금 안에서 게임 처리를 시작하지 않는다.
    public void Start() => Completion = Task.Run(RunAsync);

    public void Stop()
    {
        Volatile.Write(ref _stopping, 1);
        _timer.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync())
            {
                // 타이머 콜백에서 게임 처리를 직접 실행하지 않고 각 매치를 별도로 예약한다.
                await Task.Yield();
                if (Volatile.Read(ref _stopping) != 0 || _runtime.IsTerminal)
                    break;
                try
                {
                    _tick(_runtime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Match tick failed: MatchingId={MatchingId}", _runtime.MatchingId);
                }
            }
        }
        finally
        {
            _timer.Dispose();
        }
    }
}
