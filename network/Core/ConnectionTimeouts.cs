namespace network.core;

/// <summary>
///     연결 하나의 시계 셋. 인증 전 대기(넘기면 끊는다), 인증 뒤 유휴(패킷마다 마감을 미룬다),
///     마지막 응답 뒤 유예(응답 전송이 안 끝나도 끊는다). 콜백은 타이머 스레드에서 오므로 실제로 끊을지는
///     <see cref="UserToken" />이 자기 상태 잠금 안에서 판단한다. <see cref="Dispose" /> 뒤에는 어떤 타이머도
///     새로 서지 않는다 — 종료와 타이머 콜백의 경합을 여기서 끊는다.
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

    /// <summary>인증 창을 닫고 유휴 창을 연다. 유휴 콜백은 마감이 실제로 지났을 때만 온다.</summary>
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

    /// <summary>
    ///     패킷이 왔다 — 유휴 마감만 미룬다. 타이머를 매 패킷마다 다시 맞추지 않고, 울렸을 때 마감이 남았으면
    ///     그만큼 다시 재운다. 50ms 이동 패킷마다 Timer.Change를 부르지 않기 위해서다.
    /// </summary>
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

    public void DisarmGracefulClose()
    {
        lock (_gate)
        {
            _gracefulCloseTimer?.Dispose();
            _gracefulCloseTimer = null;
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
