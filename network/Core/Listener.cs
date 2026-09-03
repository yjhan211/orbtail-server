using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.common;

namespace network.core;

public sealed class Listener(ILogger<Listener> logger)
{
    private readonly ILogger<Listener> _logger = logger;
    public delegate void NewClientHandler(Socket clientSocket, object? token);

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Socket? _listenSocket;
    private Task? _listenTask;

    public event NewClientHandler? ClientConnected;

    public void Start(IPAddress address, short port)
    {
        lock (_lifecycleLock)
        {
            if (_listenTask is { IsCompleted: false })
                throw new InvalidOperationException("Listener is already running.");

            var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listenSocket.Bind(new IPEndPoint(address, port));
                listenSocket.Listen(Config.BACK_LOG);
            }
            catch
            {
                listenSocket.Dispose();
                throw;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _listenSocket = listenSocket;
            _listenTask = AcceptLoopAsync(listenSocket, _cts.Token);
        }
    }

    /// <summary>
    ///     매개변수 토큰은 "루프 종료를 얼마나 기다릴지"(호출자의 취소)이고, 필드 _cts는 accept 루프 자체의 취소다.
    ///     둘을 합치면 호출자가 기다림을 포기한 것과 루프가 정상 종료한 것을 구분할 수 없다.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? listenTask;
        CancellationTokenSource? cts;
        Socket? listenSocket;

        lock (_lifecycleLock)
        {
            listenTask = _listenTask;
            cts = _cts;
            listenSocket = _listenSocket;
            _listenSocket = null;
        }

        if (listenTask == null) return;

        try
        {
            await cts.CancelAsync();
            listenSocket?.Dispose();
            await listenTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // Listener cancellation is the normal stop path.
        }
        catch (ObjectDisposedException) when (cts?.IsCancellationRequested == true)
        {
            // Closing the listen socket interrupts AcceptAsync.
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_listenTask, listenTask))
                {
                    _listenTask = null;
                    _cts = null;
                }
            }

            cts?.Dispose();
        }
    }

    private async Task AcceptLoopAsync(Socket listenSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket clientSocket;
            try
            {
                clientSocket = await listenSocket.AcceptAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Listener stopped while awaiting a connection");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to accept a client connection");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            var handler = ClientConnected;
            if (handler == null)
            {
                _logger.LogWarning("Accepted a client connection without a registered handler");
                clientSocket.Dispose();
                continue;
            }

            try
            {
                handler(clientSocket, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client connection initialization failed");
                clientSocket.Dispose();
            }
        }
    }
}
