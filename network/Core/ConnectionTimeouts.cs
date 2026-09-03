namespace network.core;

/// <summary>
///     TCP 연결 하나에 필요한 시간 제한을 관리한다.
///
///     접속 후 일정 시간 안에 인증하지 않으면 연결을 종료하고,
///     인증 후에는 패킷이 들어올 때마다 유휴 제한 시간을 연장한다.
///     마지막 응답을 보낸 뒤에는 전송이 끝나지 않더라도 일정 시간이 지나면 연결을 종료한다.
///
///     타이머는 다른 스레드에서 실행될 수 있으므로,
///     실제로 연결을 종료할지는 UserToken이 현재 연결 상태를 확인한 뒤 결정한다.
///     Dispose된 이후에는 새로운 타이머를 예약하지 않는다.
/// </summary>
internal sealed class ConnectionTimeouts(
    TimeSpan authenticationWindow,
    TimeSpan idleWindow,
    TimeSpan gracefulCloseWindow) : IDisposable
{
    private readonly object _gate = new();
    private Timer? _authenticationTimer;
    private Timer? _idleTimer;
    private Timer? _gracefulCloseTimer;
    private long _idleDeadlineTicks;
    private bool _disposed;

    public void StartAuthenticationWindow(Action onTimeout)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _authenticationTimer?.Dispose();
            _authenticationTimer = new Timer(_ => onTimeout(), null, authenticationWindow, Timeout.InfiniteTimeSpan);
        }
    }

    public void MarkAuthenticated(Action onIdleTimeout)
    {
        lock (_gate)
        {
            _authenticationTimer?.Dispose();
            _authenticationTimer = null;
            if (_disposed) return;

            _idleDeadlineTicks = Environment.TickCount64 + (long)idleWindow.TotalMilliseconds;
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => OnIdleTimerFired(onIdleTimeout), null, idleWindow, Timeout.InfiniteTimeSpan);
        }
    }

    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed || _idleTimer == null) return;
            _idleDeadlineTicks = Environment.TickCount64 + (long)idleWindow.TotalMilliseconds;
        }
    }

    public void ArmGracefulClose(Action onTimeout)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _gracefulCloseTimer?.Dispose();
            _gracefulCloseTimer = new Timer(_ => onTimeout(), null, gracefulCloseWindow, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _authenticationTimer?.Dispose();
            _idleTimer?.Dispose();
            _gracefulCloseTimer?.Dispose();
            _authenticationTimer = null;
            _idleTimer = null;
            _gracefulCloseTimer = null;
        }
    }

    private void OnIdleTimerFired(Action onIdleTimeout)
    {
        lock (_gate)
        {
            if (_disposed || _idleTimer == null) return;

            long remaining = _idleDeadlineTicks - Environment.TickCount64;
            if (remaining > 0)
            {
                try
                {
                    _idleTimer.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // 종료와 콜백이 겹친 경우 — 종료가 이긴다.
                }

                return;
            }
        }

        onIdleTimeout();
    }
}
