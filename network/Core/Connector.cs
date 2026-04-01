using System.Net;
using System.Net.Sockets;
using network.managers;

// ReSharper disable All

namespace network.core;

#pragma warning disable CS9113 // 매개 변수를 읽지 않았습니다.
public class Connector(NetworkService networkService, LogManager logManager)
#pragma warning restore CS9113 // 매개 변수를 읽지 않았습니다.
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
