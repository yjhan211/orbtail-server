using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;

namespace network.core;

/// <summary>
///     클라이언트의 TCP 연결 하나를 관리한다.
///     소켓과 세션, 연결 상태를 보관하며 여러 곳에서 동시에 종료를 요청해도 종료 처리는 한 번만 실행한다.
///
///     송신 대기열은 <see cref="SendQueue"/>가 관리하고,
///     인증 및 유휴 시간 제한은 <see cref="ConnectionTimeouts"/>가 관리한다.
///     실제 패킷 수신과 송신 처리는 UserToken.Transport 파일에 분리되어 있다.
///
///     연결 상태:
///         New → Active → Closing → Released
///
///     연결 종료 과정:
///         1. 연결 상태를 Closing으로 바꾼다.
///         2. 소켓과 타이머, 송신 대기열을 정리한다.
///         3. 진행 중인 수신·송신·메시지 처리가 모두 끝날 때까지 기다린다.
///         4. 세션에 연결 종료를 알리고 I/O 객체를 풀에 반환한다.
///         5. 상태를 Released로 바꾸고 ReleaseTask를 완료한다.
///
///     클라이언트 접속 종료, 네트워크 오류, 잘못된 패킷, 타임아웃,
///     서버 종료 등 어떤 이유로 종료되더라도 같은 과정을 거친다.
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

    private int _state = StateNew;
    private int _initialized;
    private int _authenticated;
    private int _closeAfterSend;
    private int _closePrepared;
    private int _releaseSignaled;
    private int _peerNotified;

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

    public bool IsAcceptingMessages =>
        Volatile.Read(ref _state) == StateActive && Volatile.Read(ref _closeAfterSend) == 0;

    internal Task ReleaseTask => _releaseCompletion.Task;

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

    public void SetPeer(IPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Interlocked.CompareExchange(ref _peer, peer, null) != null)
            throw new InvalidOperationException("A peer is already assigned to this connection.");
    }

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

    internal void CloseTransport(Action<Exception> logException)
    {
        var socket = Interlocked.Exchange(ref _socket, null);
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

    internal void NotifyPeerClosed(Action<Exception> logException)
    {
        var peer = Volatile.Read(ref _peer);
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

    private void CompleteCloseRequest(ConnectionCloseReason reason, Exception? exception = null)
    {
        var closeStarted = Volatile.Read(ref _closeStarted);
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
