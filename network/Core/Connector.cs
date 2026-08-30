using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.managers;

namespace network.core;

public sealed class Connector(NetworkService networkService, LogManager logManager)
{
    public delegate void ConnectEventHandler(UserToken token);

    public event ConnectEventHandler? Connected;

    public void Connect(IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);

        var socket = new Socket(remoteEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        SocketAsyncEventArgs eventArgs = new()
        {
            RemoteEndPoint = remoteEndpoint,
            UserToken = socket
        };
        eventArgs.Completed += OnConnectCompleted;

        try
        {
            if (!socket.ConnectAsync(eventArgs)) OnConnectCompleted(null, eventArgs);
        }
        catch (Exception ex)
        {
            eventArgs.Completed -= OnConnectCompleted;
            eventArgs.UserToken = null;
            eventArgs.Dispose();
            socket.Dispose();
            logManager.LogError(ex, "Failed to start outbound connection to {RemoteEndpoint}", remoteEndpoint);
        }
    }

    private void OnConnectCompleted(object? _, SocketAsyncEventArgs eventArgs)
    {
        var socket = eventArgs.UserToken as Socket;
        SocketError socketError = eventArgs.SocketError;
        EndPoint? remoteEndpoint = eventArgs.RemoteEndPoint;
        eventArgs.Completed -= OnConnectCompleted;
        eventArgs.UserToken = null;
        eventArgs.Dispose();

        if (socket == null)
        {
            logManager.LogError("Outbound connection completed without an owning socket");
            return;
        }

        if (socketError != SocketError.Success)
        {
            logManager.LogWarning("Outbound connection to {RemoteEndpoint} failed: {SocketError}",
                remoteEndpoint, socketError);
            socket.Dispose();
            return;
        }

        var connected = Connected;
        if (connected == null)
        {
            logManager.LogWarning("Outbound connection completed without a registered session handler");
            socket.Dispose();
            return;
        }

        var userToken = new UserToken();
        try
        {
            networkService.OnConnectCompleted(socket, userToken);
            if (!userToken.TryBeginOperation()) return;

            try
            {
                connected(userToken);
            }
            catch (Exception ex)
            {
                logManager.LogError(ex, "Outbound session creation failed");
                userToken.Disconnect();
            }
            finally
            {
                userToken.CompleteOperation();
            }

            networkService.StartReceiving(userToken);
        }
        catch (Exception ex)
        {
            logManager.LogError(ex, "Failed to initialize outbound connection");
            userToken.Disconnect();
            socket.Dispose();
        }
    }
}
