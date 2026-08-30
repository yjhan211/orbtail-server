using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.common;

namespace network.core;

public sealed class Listener(ILogger? logger = null)
{
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
            cts?.Cancel();
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
                logger?.LogDebug(ex, "Listener stopped while awaiting a connection");
                break;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to accept a client connection");
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
                logger?.LogWarning("Accepted a client connection without a registered handler");
                clientSocket.Dispose();
                continue;
            }

            try
            {
                handler(clientSocket, null);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Client connection initialization failed");
                clientSocket.Dispose();
            }
        }
    }
}
