using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;

namespace network.core;

/// <summary>
///     TCP 연결 하나. 소켓·peer(세션)·상태를 갖고, 여러 스레드에서 동시에 오는 종료 요청을 한 번의 종료 절차로 모은다.
///     송신 대기열은 <see cref="SendQueue" />, 시계 셋은 <see cref="ConnectionTimeouts" />가 맡고,
///     프레이밍·송수신 처리는 UserToken.Transport 파셜에 있다.
///
///     상태는 한 방향으로만 간다.
///         New ──InitializeConnection──▶ Active ──TryBeginClose──▶ Closing ──MarkReleased──▶ Released
///
///     종료 절차 (어느 경로로 시작하든 같다):
///         1. TryBeginClose        Active → Closing. 여러 경로가 겹쳐도 상태 잠금 안에서 한 번만 성공한다.
///         2. closeStarted 콜백    NetworkService가 CloseTransport(소켓·시계·송신 큐 정리) → MarkClosePrepared.
///         3. 작업 계수 0          진행 중이던 수신·송신·핸들러·세션 생성이 모두 CompleteOperation을 부르면
///                                 TrySignalReleaseReady가 한 번만 releaseReady 콜백을 낸다.
///         4. releaseReady 콜백    NetworkService가 NotifyPeerClosed(세션 OnDisconnect·OnRemoved 각 한 번) →
///                                 DetachEventArgs(I/O 객체 풀 반납) → MarkReleased(ReleaseTask 완료).
///
///     종료 경로: 원격 종료·수신 오류·손상 패킷·큐 넘침·송신 오류·인증/유휴 타임아웃·명시적 끊기·서버 정지.
/// </summary>
public partial class UserToken
{
    private const int MaxPendingMessages = 128;
    private const int StateNew = 0;
    private const int StateActive = 1;
    private const int StateClosing = 2;
    private const int StateReleased = 3;

    private static readonly TimeSpan GracefulCloseWindow = TimeSpan.FromSeconds(1);

    private readonly object _stateLock = new();
    private readonly MessageResolver _messageResolver = new();
    private readonly SendQueue _sendQueue = new();
    private readonly ConnectionTimeouts _timeouts = new(
        TimeSpan.FromSeconds(Config.AUTHENTICATION_TIMEOUT_SECONDS),
        TimeSpan.FromSeconds(Config.AUTHENTICATED_IDLE_TIMEOUT_SECONDS),
        GracefulCloseWindow);

    // 상태와 한 번만 지나는 문들 — 전부 Interlocked/Volatile로 다룬다.
    private int _state = StateNew;
    private int _initialized;
    private int _authenticated;
    private int _closeAfterSend;
    private int _closePrepared;
    private int _releaseSignaled;
    private int _peerNotified;

    // 작업 계수 — 0이 돼야 자원을 반납한다.
    private int _pendingOperations;
    private int _pendingMessages;

    private Socket? _socket;
    private IPeer? _peer;
    private Action<UserToken, ConnectionCloseReason, Exception?>? _closeStarted;
    private Action<UserToken>? _releaseReady;
    private TaskCompletionSource<bool> _releaseCompletion = CreateCompletedReleaseSource();

    public SocketAsyncEventArgs? ReceiveEventArgs { get; private set; }
    private SocketAsyncEventArgs? SendEventArgs { get; set; }
    public Socket? Socket => Volatile.Read(ref _socket);
    public bool IsReleased => Volatile.Read(ref _state) != StateActive;

    /// <summary>마지막 응답을 보내고 끊기로 한 뒤에는 들어오는 패킷을 처리하지 않는다.</summary>
    public bool IsAcceptingMessages =>
        Volatile.Read(ref _state) == StateActive && Volatile.Read(ref _closeAfterSend) == 0;

    /// <summary>자원 반납까지 끝나면 완료된다. 초기화 전에는 기다릴 것이 없으므로 이미 완료 상태다.</summary>
    internal Task ReleaseTask => _releaseCompletion.Task;

    /// <summary>NetworkService가 accept 직후 한 번 부른다. 전부 채운 뒤 Active로 켜고, 그 순간부터 인증 시계가 돈다.</summary>
    internal void InitializeConnection(
        Socket socket,
        SocketAsyncEventArgs receiveEventArgs,
        SocketAsyncEventArgs sendEventArgs,
        Action<UserToken, ConnectionCloseReason, Exception?> closeStarted,
        Action<UserToken> releaseReady)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            throw new InvalidOperationException("UserToken cannot be initialized more than once.");

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        ReceiveEventArgs = receiveEventArgs ?? throw new ArgumentNullException(nameof(receiveEventArgs));
        SendEventArgs = sendEventArgs ?? throw new ArgumentNullException(nameof(sendEventArgs));
        _closeStarted = closeStarted ?? throw new ArgumentNullException(nameof(closeStarted));
        _releaseReady = releaseReady ?? throw new ArgumentNullException(nameof(releaseReady));
        _releaseCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiveEventArgs.UserToken = this;
        sendEventArgs.UserToken = this;
        Volatile.Write(ref _state, StateActive);

        _timeouts.StartAuthenticationWindow(OnAuthenticationTimeout);
    }

    /// <summary>세션이 자기를 등록한다. 수신은 이 뒤에 시작돼야 첫 패킷이 갈 곳이 있다.</summary>
    public virtual void SetPeer(IPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Interlocked.CompareExchange(ref _peer, peer, null) != null)
            throw new InvalidOperationException("A peer is already assigned to this connection.");
    }

    /// <summary>
    ///     인증 성공을 한 번만 확정한다. <paramref name="onAuthenticated" />는 상태 잠금 안에서 돌아, 세션 등록과
    ///     "아직 Active인지" 확인이 한 번에 일어난다. 그 뒤 인증 시계를 유휴 시계로 바꾼다.
    /// </summary>
    public bool TryMarkAuthenticated(Action? onAuthenticated = null)
    {
        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) != 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return false;

            onAuthenticated?.Invoke();
            Volatile.Write(ref _authenticated, 1);
        }

        _timeouts.MarkAuthenticated(OnIdleTimeout);
        return true;
    }

    /// <summary>연결이 Active이고 끊기 예약이 없을 때만 <paramref name="action" />을 상태 잠금 안에서 실행한다.</summary>
    public bool TryRunIfActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive || Volatile.Read(ref _closeAfterSend) != 0)
                return false;

            action();
            return true;
        }
    }

    public void Disconnect()
    {
        RequestClose(ConnectionCloseReason.ExplicitDisconnect);
    }

    /// <summary>
    ///     수신·송신·핸들러·세션 생성처럼 "끝나야 자원을 반납할 수 있는 일"의 시작. Active가 아니면 거절한다 —
    ///     닫기가 시작된 뒤에는 새 일이 끼어들지 못한다.
    /// </summary>
    internal bool TryBeginOperation()
    {
        Interlocked.Increment(ref _pendingOperations);
        if (Volatile.Read(ref _state) == StateActive) return true;

        CompleteOperation();
        return false;
    }

    internal void CompleteOperation()
    {
        int remaining = Interlocked.Decrement(ref _pendingOperations);
        if (remaining == 0) TrySignalReleaseReady();
    }

    /// <summary>종료 절차 1단계. 여러 경로가 겹쳐도 한 번만 true.</summary>
    internal bool TryBeginClose()
    {
        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive)
                return false;

            Volatile.Write(ref _state, StateClosing);
            return true;
        }
    }

    /// <summary>종료 절차 2단계의 본체. 소켓·시계·송신 큐를 정리한다. NetworkService가 closeStarted 콜백에서 부른다.</summary>
    internal void CloseTransport(Action<Exception> logException)
    {
        Socket? socket = Interlocked.Exchange(ref _socket, null);
        if (socket != null)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex)
            {
                ReportException(logException, ex);
            }

            try
            {
                socket.Dispose();
            }
            catch (Exception ex)
            {
                ReportException(logException, ex);
            }
        }

        _timeouts.Dispose();
        _sendQueue.Clear();
    }

    /// <summary>종료 절차 4단계. 세션에 OnDisconnect·OnRemoved를 각 한 번 알린다. peer가 없으면 할 일이 없다.</summary>
    internal void NotifyPeerClosed(Action<Exception> logException)
    {
        IPeer? peer = Volatile.Read(ref _peer);
        if (peer == null) return;
        if (Interlocked.Exchange(ref _peerNotified, 1) != 0) return;

        try
        {
            peer.OnDisconnect();
        }
        catch (Exception ex)
        {
            ReportException(logException, ex);
        }

        try
        {
            peer.OnRemoved();
        }
        catch (Exception ex)
        {
            ReportException(logException, ex);
        }
    }

    /// <summary>2단계 끝 — 전송이 닫혔다. 작업 계수가 이미 0이면 여기서 바로 반납 신호가 난다.</summary>
    internal void MarkClosePrepared()
    {
        Volatile.Write(ref _closePrepared, 1);
        TrySignalReleaseReady();
    }

    internal void DetachEventArgs(
        out SocketAsyncEventArgs? receiveEventArgs,
        out SocketAsyncEventArgs? sendEventArgs)
    {
        receiveEventArgs = ReceiveEventArgs;
        sendEventArgs = SendEventArgs;
        ReceiveEventArgs = null;
        SendEventArgs = null;
        _closeStarted = null;
        _releaseReady = null;
        _peer = null;
    }

    /// <summary>종료 절차의 끝. 정지 절차가 기다리던 ReleaseTask가 여기서 완료된다.</summary>
    internal void MarkReleased()
    {
        Volatile.Write(ref _state, StateReleased);
        _releaseCompletion.TrySetResult(true);
    }

    private void OnAuthenticationTimeout()
    {
        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) != 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return;

            Volatile.Write(ref _state, StateClosing);
        }

        CompleteCloseRequest(ConnectionCloseReason.AuthenticationTimeout);
    }

    private void OnIdleTimeout()
    {
        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) == 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return;

            Volatile.Write(ref _state, StateClosing);
        }

        CompleteCloseRequest(ConnectionCloseReason.AuthenticatedIdleTimeout);
    }

    private void RequestClose(ConnectionCloseReason reason, Exception? exception = null)
    {
        if (!TryBeginClose()) return;

        CompleteCloseRequest(reason, exception);
    }

    /// <summary>Closing으로 넘어간 뒤의 공통 경로. NetworkService가 없으면(테스트·독립 사용) 절차를 스스로 끝낸다.</summary>
    private void CompleteCloseRequest(ConnectionCloseReason reason, Exception? exception = null)
    {
        Action<UserToken, ConnectionCloseReason, Exception?>? closeStarted = Volatile.Read(ref _closeStarted);
        if (closeStarted != null)
        {
            closeStarted(this, reason, exception);
            return;
        }

        CloseTransport(_ => { });
        NotifyPeerClosed(_ => { });
        MarkClosePrepared();
        MarkReleased();
    }

    /// <summary>3단계의 문. Closing이고, 전송이 닫혔고, 진행 중 작업이 0일 때 딱 한 번 releaseReady를 낸다.</summary>
    private void TrySignalReleaseReady()
    {
        if (Volatile.Read(ref _state) != StateClosing ||
            Volatile.Read(ref _closePrepared) == 0 ||
            Volatile.Read(ref _pendingOperations) != 0 ||
            Interlocked.Exchange(ref _releaseSignaled, 1) != 0)
            return;

        _releaseReady?.Invoke(this);
    }

    private static TaskCompletionSource<bool> CreateCompletedReleaseSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private static void ReportException(Action<Exception> logException, Exception exception)
    {
        try
        {
            logException(exception);
        }
        catch
        {
            // 로거 실패가 연결 회수 자체를 막으면 안 된다.
        }
    }
}
