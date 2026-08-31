using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;

namespace network.core;

/// <summary>
///     Represents one TCP connection and owns its authentication, peer binding, close state machine, and resource
///     release contract. Packet transport is grouped in the transport partial implementation.
/// </summary>
public partial class UserToken
{
    private const int MaxQueuedSendPackets = 128;
    private const int MaxQueuedSendBytes = 256 * 1024;
    private const int MaxPendingMessages = 128;
    private const int StateNew = 0;
    private const int StateActive = 1;
    private const int StateClosing = 2;
    private const int StateReleased = 3;

    private readonly object _sendingQueueLock = new();
    private readonly object _stateTransitionLock = new();
    private readonly MessageResolver _messageResolver = new();
    private readonly Queue<Packet> _sendingQueue = new();
    private Action<UserToken, ConnectionCloseReason, Exception?>? _closeStarted;
    private int _authenticated;
    private Timer? _authenticationTimer;
    private Timer? _authenticatedIdleTimer;
    private long _authenticatedIdleDeadlineMilliseconds;
    private int _closeAfterSend;
    private Timer? _gracefulCloseTimer;
    private int _closePrepared;
    private int _disconnectNotified;
    private Timer? _heartbeatTimer;
    private int _initialized;
    private int _pendingOperations;
    private int _pendingMessages;
    private IPeer? _peer;
    private int _queuedSendBytes;
    private Action<UserToken>? _releaseReady;
    private int _releaseSignaled;
    private TaskCompletionSource<bool> _releaseCompletion = CreateCompletedReleaseSource();
    private int _removedNotified;
    private int _sendOffset;
    private Socket? _socket;
    private int _state = StateNew;

    public SocketAsyncEventArgs? ReceiveEventArgs { get; private set; }
    public SocketAsyncEventArgs? SendEventArgs { get; private set; }
    public Socket? Socket => Volatile.Read(ref _socket);
    public bool IsReleased => Volatile.Read(ref _state) != StateActive;
    public bool IsAcceptingMessages =>
        Volatile.Read(ref _state) == StateActive && Volatile.Read(ref _closeAfterSend) == 0;
    public event Action<UserToken>? Disconnected;

    internal SocketEventArgsOwnership EventArgsOwnership { get; private set; }
    internal Task ReleaseTask => _releaseCompletion.Task;

    internal void InitializeConnection(
        Socket socket,
        SocketAsyncEventArgs receiveEventArgs,
        SocketAsyncEventArgs sendEventArgs,
        SocketEventArgsOwnership eventArgsOwnership,
        Action<UserToken, ConnectionCloseReason, Exception?> closeStarted,
        Action<UserToken> releaseReady)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            throw new InvalidOperationException("UserToken cannot be initialized more than once.");

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        ReceiveEventArgs = receiveEventArgs ?? throw new ArgumentNullException(nameof(receiveEventArgs));
        SendEventArgs = sendEventArgs ?? throw new ArgumentNullException(nameof(sendEventArgs));
        EventArgsOwnership = eventArgsOwnership;
        _closeStarted = closeStarted ?? throw new ArgumentNullException(nameof(closeStarted));
        _releaseReady = releaseReady ?? throw new ArgumentNullException(nameof(releaseReady));
        _releaseCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiveEventArgs.UserToken = this;
        sendEventArgs.UserToken = this;
        Volatile.Write(ref _state, StateActive);

        if (eventArgsOwnership == SocketEventArgsOwnership.ListenerPool)
        {
            _authenticationTimer = new Timer(
                _ => RequestAuthenticationTimeoutClose(),
                null,
                TimeSpan.FromSeconds(Config.AUTHENTICATION_TIMEOUT_SECONDS),
                Timeout.InfiniteTimeSpan);
        }
    }

    public bool TryMarkAuthenticated(Action? onAuthenticated = null)
    {
        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) != 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return false;

            onAuthenticated?.Invoke();
            Volatile.Write(ref _authenticated, 1);
            Volatile.Write(
                ref _authenticatedIdleDeadlineMilliseconds,
                Environment.TickCount64 + GetAuthenticatedIdleTimeoutMilliseconds());
        }

        Interlocked.Exchange(ref _authenticationTimer, null)?.Dispose();
        if (EventArgsOwnership != SocketEventArgsOwnership.ListenerPool || IsReleased)
            return true;

        var idleTimer = new Timer(
            _ => RequestAuthenticatedIdleTimeoutClose(),
            null,
            TimeSpan.FromSeconds(Config.AUTHENTICATED_IDLE_TIMEOUT_SECONDS),
            Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _authenticatedIdleTimer, idleTimer)?.Dispose();
        if (IsReleased &&
            Interlocked.CompareExchange(ref _authenticatedIdleTimer, null, idleTimer) == idleTimer)
        {
            idleTimer.Dispose();
        }

        return true;
    }

    public bool TryRunIfActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive || Volatile.Read(ref _closeAfterSend) != 0)
                return false;

            action();
            return true;
        }
    }

    public virtual void SetPeer(IPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (Interlocked.CompareExchange(ref _peer, peer, null) != null)
            throw new InvalidOperationException("A peer is already assigned to this connection.");
    }

    public void Disconnect()
    {
        RequestClose(ConnectionCloseReason.ExplicitDisconnect);
    }

    private void ArmGracefulCloseTimer()
    {
        var gracefulCloseTimer = new Timer(
            _ => RequestClose(ConnectionCloseReason.ExplicitDisconnect),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _gracefulCloseTimer, gracefulCloseTimer)?.Dispose();
        if (IsReleased &&
            Interlocked.CompareExchange(ref _gracefulCloseTimer, null, gracefulCloseTimer) == gracefulCloseTimer)
        {
            gracefulCloseTimer.Dispose();
        }
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
        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive)
                return false;

            Volatile.Write(ref _state, StateClosing);
            return true;
        }
    }

    private void RequestAuthenticationTimeoutClose()
    {
        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) != 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return;

            Volatile.Write(ref _state, StateClosing);
        }

        CompleteCloseRequest(ConnectionCloseReason.AuthenticationTimeout);
    }

    private void RequestAuthenticatedIdleTimeoutClose()
    {
        Timer? idleTimer = null;
        TimeSpan remaining = default;
        bool shouldClose = false;

        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) == 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
                return;

            long remainingMilliseconds =
                Volatile.Read(ref _authenticatedIdleDeadlineMilliseconds) - Environment.TickCount64;
            if (remainingMilliseconds > 0)
            {
                idleTimer = Volatile.Read(ref _authenticatedIdleTimer);
                remaining = TimeSpan.FromMilliseconds(remainingMilliseconds);
            }
            else
            {
                Volatile.Write(ref _state, StateClosing);
                shouldClose = true;
            }
        }

        if (shouldClose)
        {
            CompleteCloseRequest(ConnectionCloseReason.AuthenticatedIdleTimeout);
            return;
        }

        if (idleTimer == null)
            return;

        try
        {
            idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 정상적인 연결 종료와 stale timer callback이 경합한 경우다.
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

        Interlocked.Exchange(ref _heartbeatTimer, null)?.Dispose();
        Interlocked.Exchange(ref _authenticationTimer, null)?.Dispose();
        Interlocked.Exchange(ref _authenticatedIdleTimer, null)?.Dispose();
        Interlocked.Exchange(ref _gracefulCloseTimer, null)?.Dispose();

        lock (_sendingQueueLock)
        {
            while (_sendingQueue.Count > 0)
                _sendingQueue.Dequeue().Dispose();
            _queuedSendBytes = 0;
            _sendOffset = 0;
        }

    }

    internal void NotifyPeerClosed(Action<Exception> logException)
    {
        var peer = Volatile.Read(ref _peer);
        if (peer == null) return;

        if (Interlocked.Exchange(ref _disconnectNotified, 1) == 0)
        {
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
                Disconnected?.Invoke(this);
            }
            catch (Exception ex)
            {
                ReportException(logException, ex);
            }
        }

        if (Interlocked.Exchange(ref _removedNotified, 1) != 0) return;

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
        out SocketAsyncEventArgs? sendEventArgs,
        out SocketEventArgsOwnership ownership)
    {
        receiveEventArgs = ReceiveEventArgs;
        sendEventArgs = SendEventArgs;
        ownership = EventArgsOwnership;
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

    private static long GetAuthenticatedIdleTimeoutMilliseconds() =>
        checked((long)Config.AUTHENTICATED_IDLE_TIMEOUT_SECONDS * 1000L);

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
