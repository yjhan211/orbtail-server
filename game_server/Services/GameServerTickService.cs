using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     서버 전체가 공유하는 매치 틱(50ms)과 구역 폐쇄 틱(1초)의 시작·종료를 관리한다.
///     매치 상태와 잠금은 전달받은 처리기가 담당한다.
///     종료 시 두 타이머를 해제하고 실행 중인 콜백이 모두 끝날 때까지 기다린다.
/// </summary>
internal sealed class GameServerTickService(
    ILogger<GameServerTickService> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MatchTickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AreaClosureInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _lifecycleLock = new();
    private ITimer? _matchTimer;
    private ITimer? _areaClosureTimer;
    private bool _started;
    private Task? _stopTask;

    public void Start(Action areaClosureTick, Action matchTick)
    {
        ArgumentNullException.ThrowIfNull(areaClosureTick);
        ArgumentNullException.ThrowIfNull(matchTick);
        lock (_lifecycleLock)
        {
            if (_started || _stopTask != null)
                throw new InvalidOperationException("Game server ticks cannot be started again.");
            _started = true;
            _areaClosureTimer = _timeProvider.CreateTimer(
                _ => RunTick(areaClosureTick, "area closure"), null,
                AreaClosureInterval, AreaClosureInterval);
            _matchTimer = _timeProvider.CreateTimer(
                _ => RunTick(matchTick, "match"), null,
                MatchTickInterval, MatchTickInterval);
        }
        logger.LogInformation("Game server ticks started: MatchMs=50, AreaClosureMs=1000");
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            // 두 타이머 모두 해제를 요청한 뒤 함께 기다린다.
            return _stopTask ??= Task.WhenAll(
                _areaClosureTimer?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _matchTimer?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        }
    }

    private void RunTick(Action tick, string name)
    {
        try
        {
            tick();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Game server tick failed: Tick={Tick}", name);
        }
    }
}
