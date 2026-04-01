using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using network.common;
using network.interfaces;

namespace network.core;

public class NetworkService : INetworkService
{
    private readonly BufferManager _bufferManager;
    private readonly Listener _clientListener = new();
    private readonly object _initEventArgsLock = new();
    private readonly ILogger? _logger;
    private readonly SocketAsyncEventArgsManager _recvEventArgsManager;
    private readonly SocketAsyncEventArgsManager _sendEventArgsManager;

    public NetworkService(ILogger<NetworkService>? logger = null)
    {
        _logger = logger;
        _bufferManager = new BufferManager(Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
            Config.BUFFER_SIZE);
        _recvEventArgsManager = new SocketAsyncEventArgsManager(Config.MAX_CONNECTION);
        _sendEventArgsManager = new SocketAsyncEventArgsManager(Config.MAX_CONNECTION);

        InitializeEventArgs();
    }

    public Action<UserToken>? SessionCreatedCallback { get; set; }

    public void Listen(IPAddress address, short port)
    {
        _clientListener.ClientConnected += (socket, obj) => OnNewClient(socket, obj!);
        _clientListener.Start(address, port);
    }

    // from Connector
    public void OnConnectCompleted(Socket socket, UserToken userToken)
    {
        SocketAsyncEventArgs recvArgs = new();
        recvArgs.Completed += (sender, e) => RecvCompleted(sender!, e);
        recvArgs.UserToken = userToken;
        recvArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

        SocketAsyncEventArgs sendArgs = new();
        sendArgs.Completed += (sender, e) => SendCompleted(sender!, e);
        sendArgs.UserToken = userToken;
        sendArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

        BeginRecv(userToken, socket, recvArgs, sendArgs);
    }

    public void CloseClientSocket(UserToken? userToken)
    {
        if (userToken == null || userToken.IsReleased) return;

        userToken.Disconnect();

        lock (_initEventArgsLock)
        {
            _recvEventArgsManager.Push(userToken.RecvEventArgs!);
            _sendEventArgsManager.Push(userToken.SendEventArgs!);
        }
    }

    private void InitializeEventArgs()
    {
        for (int i = 0; i < Config.MAX_CONNECTION; i++)
        {
            var userToken = new UserToken();

            SocketAsyncEventArgs recvArgs = new();
            recvArgs.Completed += (sender, e) => RecvCompleted(sender!, e);
            recvArgs.UserToken = userToken;
            _bufferManager.SetBuffer(recvArgs);
            _recvEventArgsManager.Push(recvArgs);

            SocketAsyncEventArgs sendArgs = new();
            sendArgs.Completed += (sender, e) => SendCompleted(sender!, e);
            sendArgs.UserToken = userToken;
            _bufferManager.SetBuffer(sendArgs);
            _sendEventArgsManager.Push(sendArgs);
        }
    }

    // from Listener
    private void OnNewClient(Socket clientSocket, object _)
    {
        SocketAsyncEventArgs recvArgs;
        SocketAsyncEventArgs sendArgs;

        lock (_initEventArgsLock)
        {
            recvArgs = _recvEventArgsManager.Pop();
            sendArgs = _sendEventArgsManager.Pop();
        }

        object? argsToken = recvArgs.UserToken;
        if (argsToken == null) throw new Exception("[OnNewClient] Invalid UserToken");


        var userToken = (UserToken)argsToken;
        SessionCreatedCallback?.Invoke(userToken);
        BeginRecv(userToken, clientSocket, recvArgs, sendArgs);
    }

    private void BeginRecv(UserToken userToken, Socket socket, SocketAsyncEventArgs recvArgs,
        SocketAsyncEventArgs sendArgs)
    {
        userToken.SetEventArgs(recvArgs, sendArgs);
        userToken.Socket = socket;

        bool willRaiseEvent = socket.ReceiveAsync(recvArgs);
        if (!willRaiseEvent) ProcessRecv(recvArgs);
    }

    private void RecvCompleted(object _, SocketAsyncEventArgs args)
    {
        if (args.LastOperation != SocketAsyncOperation.Receive)
            throw new ArgumentException("The last operation completed on the socket was not a receive.");

        ProcessRecv(args);
    }

    private void ProcessRecv(SocketAsyncEventArgs recvArgs)
    {
        UserToken? userToken = null;
        try
        {
            if (recvArgs.UserToken is not UserToken token) return;

            userToken = token;
            if (recvArgs.SocketError != SocketError.Success)
            {
                userToken.OnRemoved();
                return;
            }

            if (recvArgs.Buffer == null) throw new Exception("[ProcessRecv] invalid Buffer");

            if (recvArgs.BytesTransferred <= 0)
            {
                userToken.OnRemoved();
                return;
            }

            (var errorCode, string? _) =
                userToken.OnReceived(recvArgs.Buffer, recvArgs.Offset, recvArgs.BytesTransferred);
            if (errorCode != ErrorCode.SUCCESS) return;

            // 다음 패킷 수신 대기
            bool willRaiseEvent = userToken.Socket!.ReceiveAsync(recvArgs);
            if (!willRaiseEvent) ProcessRecv(recvArgs);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "패킷 처리 중 오류, 세션 제거");
            userToken?.OnRemoved();
        }
    }

    private void SendCompleted(object _, SocketAsyncEventArgs sendArgs)
    {
        if (sendArgs.UserToken is not UserToken token)
            throw new Exception($"invalid args.UserToken : {sendArgs.UserToken}");
        token.ProcessSend(sendArgs);
    }
}
