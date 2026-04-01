using System.Net;
using System.Net.Sockets;
using network.managers;

namespace network.core;

public class Connector(NetworkService networkService, LogManager logManager)
{
    public delegate void ConnectEventHandler(UserToken token);

    private Socket? _client;

    public event ConnectEventHandler? Connected;

    public void Connect(IPEndPoint remoteEndpoint)
    {
        SocketAsyncEventArgs eventArgs = new();
        eventArgs.Completed += OnConnectCompleted;
        eventArgs.RemoteEndPoint = remoteEndpoint;

        _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (!_client.ConnectAsync(eventArgs)) OnConnectCompleted(null, eventArgs);
    }

    private void OnConnectCompleted(object? sender, SocketAsyncEventArgs args)
    {
        if (args.SocketError != SocketError.Success || _client == null || Connected == null)
            throw new Exception($"[Connector/OnConnectCompleted] {args.SocketError}");

        var token = new UserToken();
        networkService.OnConnectCompleted(_client, token);
        Connected(token);
    }
}
