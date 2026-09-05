using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.common;

namespace network.core;

/// <summary>
///     서버의 TCP 연결 생성과 종료 과정을 관리한다.
///
///     Listener가 새 소켓을 수락하면 수신·송신용 SocketAsyncEventArgs를 풀에서 빌리고,
///     연결을 나타내는 TcpConnection과 서버별 세션을 연결한 뒤 수신을 시작한다.
///
///     활성 연결을 추적하며 서버 종료 시 새로운 접속을 중단하고,
///     모든 연결의 송수신과 세션 정리가 끝날 때까지 기다린 후 SocketAsyncEventArgs를 폐기한다.
/// </summary>
public sealed class NetworkService
{
    private readonly ConcurrentDictionary<TcpConnection, byte> _activeConnections = new();
    private readonly Listener _clientListener;
    private readonly object _connectionLifecycleLock = new();
    private readonly SocketEventArgsPool _eventArgsPool;
    private readonly ILogger<NetworkService> _logger;
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _stopping;

    public NetworkService(
        ILogger<NetworkService> logger,
        ILogger<Listener> listenerLogger)
    {
        _logger = logger;
        _clientListener = new Listener(listenerLogger);
        _clientListener.ClientConnected += OnNewClient;
        _eventArgsPool = new SocketEventArgsPool(
            Config.MAX_CONNECTION,
            Config.PRE_ALLOC_COUNT,
            Config.BUFFER_SIZE,
            ReceiveCompleted,
            SendCompleted,
            () => Volatile.Read(ref _stopping) == 0);
    }

    public Func<TcpConnection, IConnectionSession?>? SessionFactory { get; set; }

    public void Listen(IPAddress address, int port)
    {
        lock (_connectionLifecycleLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                throw new InvalidOperationException("A stopped network service cannot listen again.");

            _clientListener.Start(address, port);
        }
    }

    public void CloseClientSocket(TcpConnection? connection)
    {
        connection?.Disconnect();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(CancellationToken.None);
        try
        {
            var shutdownFailures = new List<Exception>();
            lock (_connectionLifecycleLock)
            {
                Volatile.Write(ref _stopping, 1);
            }

            try
            {
                await _clientListener.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            // 유휴 풀은 즉시 비우고, 사용 중인 EventArgs는 각 연결의 release 단계에서 폐기한다.
            try
            {
                _eventArgsPool.DisposeIdle();
            }
            catch (Exception ex)
            {
                shutdownFailures.Add(ex);
            }

            var connections = _activeConnections.Keys.ToArray();
            var releaseTasks = connections.Select(connection => connection.ReleaseTask).ToArray();
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
                _eventArgsPool.DisposeIdle();
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

    private void StartReceiving(TcpConnection connection)
    {
        while (true)
        {
            if (!connection.TryBeginOperation()) return;

            var socket = connection.Socket;
            var receiveArgs = connection.ReceiveEventArgs;
            if (socket == null || receiveArgs == null)
            {
                CloseAndRelease(connection, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("Receive resources are not initialized."));
                connection.CompleteOperation();
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.ReceiveAsync(receiveArgs);
            }
            catch (Exception ex)
            {
                CloseAndRelease(connection, ConnectionCloseReason.ReceiveError, ex);
                connection.CompleteOperation();
                return;
            }

            if (willRaiseEvent) return;
            if (!ProcessReceive(receiveArgs)) return;
        }
    }

    private void OnNewClient(Socket clientSocket, object? _)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            clientSocket.Dispose();
            return;
        }

        if (!_eventArgsPool.TryRent(out var receiveArgs, out var sendArgs))
        {
            _logger.LogWarning("Connection rejected because the socket event-args pool is exhausted");
            clientSocket.Dispose();
            return;
        }

        var connection = new TcpConnection();
        try
        {
            if (!TryRegisterConnection(connection, clientSocket, receiveArgs!, sendArgs!))
            {
                _eventArgsPool.Return(receiveArgs, sendArgs);
                clientSocket.Dispose();
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize an accepted connection");
            _eventArgsPool.Return(receiveArgs, sendArgs);
            clientSocket.Dispose();
            return;
        }

        // 세션 생성과 결합도 연결 작업으로 계수하여, 생성 중 close가 EventArgs를 먼저 회수하지 않게 한다.
        if (!connection.TryBeginOperation()) return;
        try
        {
            var sessionFactory = SessionFactory;
            if (sessionFactory == null)
            {
                CloseAndRelease(connection, ConnectionCloseReason.SessionCreationFailed,
                    new InvalidOperationException("SessionFactory is not registered."));
                return;
            }

            var session = sessionFactory(connection);
            if (session == null)
            {
                CloseAndRelease(connection, ConnectionCloseReason.SessionCreationFailed,
                    new InvalidOperationException("SessionFactory did not create a session."));
                return;
            }

            connection.SetSession(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session creation failed");
            CloseAndRelease(connection, ConnectionCloseReason.SessionCreationFailed, ex);
        }
        finally
        {
            connection.CompleteOperation();
        }

        StartReceiving(connection);
    }

    private bool TryRegisterConnection(
        TcpConnection connection,
        Socket socket,
        SocketAsyncEventArgs receiveArgs,
        SocketAsyncEventArgs sendArgs)
    {
        lock (_connectionLifecycleLock)
        {
            if (Volatile.Read(ref _stopping) != 0) return false;

            connection.InitializeConnection(
                socket,
                receiveArgs,
                sendArgs,
                CloseAndReleaseStarted,
                ReleaseConnection);

            if (!_activeConnections.TryAdd(connection, 0))
                throw new InvalidOperationException("The connection is already registered.");

            return true;
        }
    }

    private void ReceiveCompleted(object? _, SocketAsyncEventArgs receiveArgs)
    {
        if (receiveArgs.UserToken is not TcpConnection connection)
        {
            _logger.LogWarning("Receive completion arrived without an owning connection");
            return;
        }

        try
        {
            if (ProcessReceive(receiveArgs)) StartReceiving(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected receive completion failure");
            CloseAndRelease(connection, ConnectionCloseReason.ReceiveError, ex);
        }
    }

    private bool ProcessReceive(SocketAsyncEventArgs receiveArgs)
    {
        if (receiveArgs.UserToken is not TcpConnection connection) return false;

        try
        {
            if (receiveArgs.LastOperation != SocketAsyncOperation.Receive)
            {
                CloseAndRelease(connection, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("A non-receive operation reached the receive callback."));
                return false;
            }

            if (receiveArgs.SocketError != SocketError.Success)
            {
                CloseAndRelease(connection, ConnectionCloseReason.ReceiveError,
                    new SocketException((int)receiveArgs.SocketError));
                return false;
            }

            if (receiveArgs.BytesTransferred == 0)
            {
                CloseAndRelease(connection, ConnectionCloseReason.RemoteClosed, null);
                return false;
            }

            if (receiveArgs.Buffer == null)
            {
                CloseAndRelease(connection, ConnectionCloseReason.ReceiveError,
                    new InvalidOperationException("The receive buffer is missing."));
                return false;
            }

            (var errorCode, string? errorLog) = connection.OnReceived(
                receiveArgs.Buffer,
                receiveArgs.Offset,
                receiveArgs.BytesTransferred);
            if (errorCode == ErrorCode.SUCCESS) return true;

            _logger.LogWarning("Malformed packet closed the connection: {Reason}", errorLog);
            CloseAndRelease(connection, ConnectionCloseReason.MalformedPacket, null);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Packet processing failed; closing the connection");
            CloseAndRelease(connection, ConnectionCloseReason.MalformedPacket, ex);
            return false;
        }
        finally
        {
            connection.CompleteOperation();
        }
    }

    private void SendCompleted(object? _, SocketAsyncEventArgs sendArgs)
    {
        if (sendArgs.UserToken is not TcpConnection connection)
        {
            _logger.LogWarning("Send completion arrived without an owning connection");
            return;
        }

        try
        {
            connection.ProcessSend(sendArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected send completion failure");
            CloseAndRelease(connection, ConnectionCloseReason.SendError, ex);
        }
    }

    private void CloseAndRelease(
        TcpConnection connection,
        ConnectionCloseReason reason,
        Exception? exception)
    {
        if (!connection.TryBeginClose()) return;

        CloseAndReleaseStarted(connection, reason, exception);
    }

    private void CloseAndReleaseStarted(
        TcpConnection connection,
        ConnectionCloseReason reason,
        Exception? exception)
    {

        try
        {
            if (reason is ConnectionCloseReason.SendQueueOverflow or ConnectionCloseReason.MessageQueueOverflow)
                _logger.LogWarning("Closing connection because a bounded queue overflowed: {Reason}", reason);
            else if (exception == null)
                _logger.LogDebug("Closing connection: {Reason}", reason);
            else
                _logger.LogDebug(exception, "Closing connection: {Reason}", reason);
        }
        catch
        {
            // 로깅 실패가 연결 정리를 막으면 안 된다.
        }

        try
        {
            connection.CloseTransport(ex => _logger.LogDebug(ex, "Exception while closing socket resources"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while closing socket resources");
        }
        finally
        {
            // 세션 콜백은 모든 생성/수신/송신/메시지 작업이 끝난 release 단계에서 실행한다.
            connection.MarkClosePrepared();
        }
    }

    private void ReleaseConnection(TcpConnection connection)
    {
        // 세션 생성 중 close된 경우 세션이 늦게 연결될 수 있으므로 release 직전에 한 번 더 보장한다.
        connection.NotifySessionClosed(ex => _logger.LogError(ex, "Session close callback failed"));
        connection.DetachEventArgs(out var receiveArgs, out var sendArgs);
        try
        {
            _eventArgsPool.Return(receiveArgs, sendArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release socket event-args resources");
            receiveArgs?.Dispose();
            sendArgs?.Dispose();
        }
        finally
        {
            connection.MarkReleased();
            // 종료 콜백과 I/O 자원 회수가 모두 끝난 뒤 활성 연결 목록에서 제거한다.
            _activeConnections.TryRemove(connection, out _);
        }
    }
}
