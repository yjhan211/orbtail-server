using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.common;
using network.interfaces;

namespace network.core;

public sealed class NetworkService : INetworkService
{
    private readonly ConcurrentDictionary<UserToken, byte> _activeConnections = new();
    private readonly BufferManager _bufferManager;
    private readonly Listener _clientListener;
    private readonly object _connectionLifecycleLock = new();
    private readonly object _eventArgsPoolLock = new();
    private readonly ILogger? _logger;
    private readonly SocketAsyncEventArgsManager _receiveEventArgsManager;
    private readonly SocketAsyncEventArgsManager _sendEventArgsManager;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _stopping;

    public NetworkService(ILogger<NetworkService>? logger = null)
    {
        _logger = logger;
        _clientListener = new Listener(logger);
        _clientListener.ClientConnected += OnNewClient;
        _bufferManager = new BufferManager(Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
            Config.BUFFER_SIZE);
        _receiveEventArgsManager = new SocketAsyncEventArgsManager(Config.MAX_CONNECTION);
        _sendEventArgsManager = new SocketAsyncEventArgsManager(Config.MAX_CONNECTION);

        InitializeEventArgs();
    }

    public Action<UserToken>? SessionCreatedCallback { get; set; }

    public void Listen(IPAddress address, short port)
    {
        lock (_connectionLifecycleLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                throw new InvalidOperationException("A stopped network service cannot listen again.");

            _clientListener.Start(address, port);
        }
    }

    // Connector가 만든 소켓과 EventArgs는 풀에 반환하지 않고 연결 종료 시 폐기한다.
    public void OnConnectCompleted(Socket socket, UserToken userToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(userToken);

        SocketAsyncEventArgs? receiveArgs = null;
        SocketAsyncEventArgs? sendArgs = null;
        try
        {
            receiveArgs = CreateConnectionOwnedEventArgs(ReceiveCompleted);
            sendArgs = CreateConnectionOwnedEventArgs(SendCompleted);
            if (!TryRegisterConnection(
                    userToken,
                    socket,
                    receiveArgs,
                    sendArgs,
                    SocketEventArgsOwnership.ConnectionOwned))
            {
                receiveArgs.Dispose();
                sendArgs.Dispose();
                socket.Dispose();
            }
        }
        catch
        {
            receiveArgs?.Dispose();
            sendArgs?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    public void CloseClientSocket(UserToken? userToken)
    {
        userToken?.Disconnect();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(CancellationToken.None);
        try
        {
            var shutdownFailures = new List<Exception>();
            lock (_connectionLifecycleLock)
            {
                Interlocked.Exchange(ref _stopping, 1);
            }

            try
            {
                await _clientListener.StopAsync();
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            // 유휴 풀은 즉시 비우고, 사용 중인 EventArgs는 각 연결의 release 단계에서 폐기한다.
            try
            {
                DisposeIdleEventArgs();
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            UserToken[] connections = _activeConnections.Keys.ToArray();
            Task[] releaseTasks = connections.Select(connection => connection.ReleaseTask).ToArray();
            foreach (var connection in connections)
            {
                try
                {
                    CloseAndRelease(connection, ConnectionCloseReason.ServerStopping, null);
                }
                catch (Exception ex)
                {
                    shutdownFailures.Add(ex);
                }
            }

            try
            {
                await Task.WhenAll(releaseTasks).WaitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            // Stop 도중 풀로 돌아온 항목이 있더라도 남기지 않는다.
            try
            {
                DisposeIdleEventArgs();
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            if (shutdownFailures.Count > 0)
                throw new AggregateException("Network shutdown completed with cleanup failures.", shutdownFailures);
        }
        finally
        {
            _stopLock.Release();
        }
    }

    internal void StartReceiving(UserToken userToken)
    {
        while (true)
        {
            if (!userToken.TryBeginOperation()) return;

            var socket = userToken.Socket;
            var receiveArgs = userToken.ReceiveEventArgs;
            if (socket == null || receiveArgs == null)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("Receive resources are not initialized."));
                userToken.CompleteOperation();
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.ReceiveAsync(receiveArgs);
            }
            catch (Exception ex)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError, ex);
                userToken.CompleteOperation();
                return;
            }

            if (willRaiseEvent) return;
            if (!ProcessReceive(receiveArgs)) return;
        }
    }

    private void InitializeEventArgs()
    {
        for (int i = 0; i < Config.MAX_CONNECTION; i++)
        {
            SocketAsyncEventArgs receiveArgs = new();
            receiveArgs.Completed += ReceiveCompleted;
            _bufferManager.SetBuffer(receiveArgs);
            _receiveEventArgsManager.Push(receiveArgs);

            SocketAsyncEventArgs sendArgs = new();
            sendArgs.Completed += SendCompleted;
            _bufferManager.SetBuffer(sendArgs);
            _sendEventArgsManager.Push(sendArgs);
        }
    }

    private SocketAsyncEventArgs CreateConnectionOwnedEventArgs(
        EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        SocketAsyncEventArgs eventArgs = new();
        eventArgs.Completed += completedHandler;
        eventArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);
        return eventArgs;
    }

    private void OnNewClient(Socket clientSocket, object? _)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            clientSocket.Dispose();
            return;
        }

        if (!TryRentPooledEventArgs(out var receiveArgs, out var sendArgs))
        {
            _logger?.LogWarning("Connection rejected because the socket event-args pool is exhausted");
            clientSocket.Dispose();
            return;
        }

        var userToken = new UserToken();
        try
        {
            if (!TryRegisterConnection(
                    userToken,
                    clientSocket,
                    receiveArgs!,
                    sendArgs!,
                    SocketEventArgsOwnership.ListenerPool))
            {
                ReturnPooledEventArgs(receiveArgs, sendArgs);
                clientSocket.Dispose();
                return;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize an accepted connection");
            ReturnPooledEventArgs(receiveArgs, sendArgs);
            clientSocket.Dispose();
            return;
        }

        // 생성 콜백도 연결 작업으로 계수하여, 생성자 실패 중 close가 EventArgs를 먼저 회수하지 않게 한다.
        if (!userToken.TryBeginOperation()) return;
        try
        {
            var sessionCreated = SessionCreatedCallback;
            if (sessionCreated == null)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.SessionCreationFailed,
                    new InvalidOperationException("SessionCreatedCallback is not registered."));
                return;
            }

            sessionCreated(userToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Session creation failed");
            CloseAndRelease(userToken, ConnectionCloseReason.SessionCreationFailed, ex);
        }
        finally
        {
            userToken.CompleteOperation();
        }

        StartReceiving(userToken);
    }

    private bool TryRegisterConnection(
        UserToken userToken,
        Socket socket,
        SocketAsyncEventArgs receiveArgs,
        SocketAsyncEventArgs sendArgs,
        SocketEventArgsOwnership ownership)
    {
        lock (_connectionLifecycleLock)
        {
            if (Volatile.Read(ref _stopping) != 0) return false;

            userToken.InitializeConnection(
                socket,
                receiveArgs,
                sendArgs,
                ownership,
                CloseAndReleaseStarted,
                ReleaseConnection);

            if (!_activeConnections.TryAdd(userToken, 0))
                throw new InvalidOperationException("The connection token is already registered.");

            return true;
        }
    }

    private bool TryRentPooledEventArgs(
        out SocketAsyncEventArgs? receiveArgs,
        out SocketAsyncEventArgs? sendArgs)
    {
        lock (_eventArgsPoolLock)
        {
            bool hasReceiveArgs = _receiveEventArgsManager.TryPop(out receiveArgs);
            bool hasSendArgs = _sendEventArgsManager.TryPop(out sendArgs);
            if (hasReceiveArgs && hasSendArgs) return true;

            if (receiveArgs != null) _receiveEventArgsManager.Push(receiveArgs);
            if (sendArgs != null) _sendEventArgsManager.Push(sendArgs);
            receiveArgs = null;
            sendArgs = null;
            return false;
        }
    }

    private void ReturnPooledEventArgs(
        SocketAsyncEventArgs? receiveArgs,
        SocketAsyncEventArgs? sendArgs)
    {
        if (receiveArgs == null || sendArgs == null)
        {
            receiveArgs?.Dispose();
            sendArgs?.Dispose();
            return;
        }

        receiveArgs.UserToken = null;
        sendArgs.UserToken = null;
        lock (_eventArgsPoolLock)
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                _receiveEventArgsManager.Push(receiveArgs);
                _sendEventArgsManager.Push(sendArgs);
                return;
            }
        }

        receiveArgs.Dispose();
        sendArgs.Dispose();
    }

    private void DisposeIdleEventArgs()
    {
        lock (_eventArgsPoolLock)
        {
            _receiveEventArgsManager.DisposeAll();
            _sendEventArgsManager.DisposeAll();
        }
    }

    private void ReceiveCompleted(object? _, SocketAsyncEventArgs receiveArgs)
    {
        if (receiveArgs.UserToken is not UserToken userToken)
        {
            _logger?.LogWarning("Receive completion arrived without an owning connection");
            return;
        }

        try
        {
            if (ProcessReceive(receiveArgs)) StartReceiving(userToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected receive completion failure");
            CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError, ex);
        }
    }

    private bool ProcessReceive(SocketAsyncEventArgs receiveArgs)
    {
        if (receiveArgs.UserToken is not UserToken userToken) return false;

        try
        {
            if (receiveArgs.LastOperation != SocketAsyncOperation.Receive)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("A non-receive operation reached the receive callback."));
                return false;
            }

            if (receiveArgs.SocketError != SocketError.Success)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError,
                    new SocketException((int)receiveArgs.SocketError));
                return false;
            }

            if (receiveArgs.BytesTransferred == 0)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.RemoteClosed, null);
                return false;
            }

            if (receiveArgs.Buffer == null)
            {
                CloseAndRelease(userToken, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("The receive buffer is missing."));
                return false;
            }

            (ErrorCode errorCode, string? errorLog) = userToken.OnReceived(
                receiveArgs.Buffer,
                receiveArgs.Offset,
                receiveArgs.BytesTransferred);
            if (errorCode == ErrorCode.SUCCESS) return true;

            _logger?.LogWarning("Malformed packet closed the connection: {Reason}", errorLog);
            CloseAndRelease(userToken, ConnectionCloseReason.MalformedPacket, null);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Packet processing failed; closing the connection");
            CloseAndRelease(userToken, ConnectionCloseReason.MalformedPacket, ex);
            return false;
        }
        finally
        {
            userToken.CompleteOperation();
        }
    }

    private void SendCompleted(object? _, SocketAsyncEventArgs sendArgs)
    {
        if (sendArgs.UserToken is not UserToken userToken)
        {
            _logger?.LogWarning("Send completion arrived without an owning connection");
            return;
        }

        try
        {
            userToken.ProcessSend(sendArgs);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected send completion failure");
            CloseAndRelease(userToken, ConnectionCloseReason.SendError, ex);
        }
    }

    private void CloseAndRelease(
        UserToken userToken,
        ConnectionCloseReason reason,
        Exception? exception)
    {
        if (!userToken.TryBeginClose()) return;

        CloseAndReleaseStarted(userToken, reason, exception);
    }

    private void CloseAndReleaseStarted(
        UserToken userToken,
        ConnectionCloseReason reason,
        Exception? exception)
    {

        try
        {
            if (reason is ConnectionCloseReason.SendQueueOverflow or ConnectionCloseReason.MessageQueueOverflow)
                _logger?.LogWarning("Closing connection because a bounded queue overflowed: {Reason}", reason);
            else if (exception == null)
                _logger?.LogDebug("Closing connection: {Reason}", reason);
            else
                _logger?.LogDebug(exception, "Closing connection: {Reason}", reason);
        }
        catch
        {
            // 로깅 실패가 연결 정리를 막으면 안 된다.
        }

        try
        {
            userToken.CloseTransport(ex => _logger?.LogDebug(ex, "Exception while closing socket resources"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected failure while closing socket resources");
        }
        finally
        {
            // 세션 콜백은 모든 생성/수신/송신/메시지 작업이 끝난 release 단계에서 실행한다.
            userToken.MarkClosePrepared();
        }
    }

    private void ReleaseConnection(UserToken userToken)
    {
        // 세션 생성 중 close된 경우 peer가 늦게 연결될 수 있으므로 release 직전에 한 번 더 보장한다.
        userToken.NotifyPeerClosed(ex => _logger?.LogError(ex, "Session close callback failed"));
        userToken.DetachEventArgs(out var receiveArgs, out var sendArgs, out var ownership);
        try
        {
            if (ownership == SocketEventArgsOwnership.ListenerPool)
            {
                ReturnPooledEventArgs(receiveArgs, sendArgs);
                return;
            }

            DisposeConnectionOwnedEventArgs(receiveArgs, ReceiveCompleted);
            DisposeConnectionOwnedEventArgs(sendArgs, SendCompleted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to release socket event-args resources");
            receiveArgs?.Dispose();
            sendArgs?.Dispose();
        }
        finally
        {
            userToken.MarkReleased();
            // Keep the connection discoverable by StopAsync until OnRemoved, EventArgs release,
            // and ReleaseTask completion are all final. Otherwise a natural close can disappear
            // from the shutdown snapshot while its cleanup still uses Redis or NATS dependencies.
            _activeConnections.TryRemove(userToken, out _);
        }
    }

    private static void DisposeConnectionOwnedEventArgs(
        SocketAsyncEventArgs? eventArgs,
        EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        if (eventArgs == null) return;
        eventArgs.UserToken = null;
        eventArgs.Completed -= completedHandler;
        eventArgs.Dispose();
    }
}
