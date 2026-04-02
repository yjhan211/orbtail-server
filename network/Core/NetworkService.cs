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
    private readonly SocketAsyncEventArgsManager _receiveEventArgsManager;
    private readonly SocketAsyncEventArgsManager _sendEventArgsManager;

    public NetworkService(ILogger<NetworkService>? logger = null)
    {
        _logger = logger;
        _bufferManager = new BufferManager(Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
            Config.BUFFER_SIZE);
        _receiveEventArgsManager = new SocketAsyncEventArgsManager(Config.MAX_CONNECTION);
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
        SocketAsyncEventArgs receiveArgs = new();
        receiveArgs.Completed += (sender, e) => ReceiveCompleted(sender!, e);
        receiveArgs.UserToken = userToken;
        receiveArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

        SocketAsyncEventArgs sendArgs = new();
        sendArgs.Completed += (sender, e) => SendCompleted(sender!, e);
        sendArgs.UserToken = userToken;
        sendArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

        BeginReceive(userToken, socket, receiveArgs, sendArgs);
    }

    public void CloseClientSocket(UserToken? userToken)
    {
        if (userToken == null || userToken.IsReleased) return;

        userToken.Disconnect();

        lock (_initEventArgsLock)
        {
            _receiveEventArgsManager.Push(userToken.ReceiveEventArgs!);
            _sendEventArgsManager.Push(userToken.SendEventArgs!);
        }
    }

    private void InitializeEventArgs()
    {
        for (int i = 0; i < Config.MAX_CONNECTION; i++)
        {
            var userToken = new UserToken();

            SocketAsyncEventArgs receiveArgs = new();
            receiveArgs.Completed += (sender, e) => ReceiveCompleted(sender!, e);
            receiveArgs.UserToken = userToken;
            _bufferManager.SetBuffer(receiveArgs);
            _receiveEventArgsManager.Push(receiveArgs);

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
        SocketAsyncEventArgs receiveArgs;
        SocketAsyncEventArgs sendArgs;

        lock (_initEventArgsLock)
        {
            receiveArgs = _receiveEventArgsManager.Pop();
            sendArgs = _sendEventArgsManager.Pop();
        }

        object? argsToken = receiveArgs.UserToken;
        if (argsToken == null) throw new Exception("[OnNewClient] Invalid UserToken");


        var userToken = (UserToken)argsToken;
        SessionCreatedCallback?.Invoke(userToken);
        BeginReceive(userToken, clientSocket, receiveArgs, sendArgs);
    }

    private void BeginReceive(UserToken userToken, Socket socket, SocketAsyncEventArgs receiveArgs,
        SocketAsyncEventArgs sendArgs)
    {
        userToken.SetEventArgs(receiveArgs, sendArgs);
        userToken.Socket = socket;

        bool willRaiseEvent = socket.ReceiveAsync(receiveArgs);
        if (!willRaiseEvent) ProcessReceive(receiveArgs);
    }

    private void ReceiveCompleted(object _, SocketAsyncEventArgs args)
    {
        if (args.LastOperation != SocketAsyncOperation.Receive)
            throw new ArgumentException("The last operation completed on the socket was not a receive.");

        ProcessReceive(args);
    }

    private void ProcessReceive(SocketAsyncEventArgs receiveArgs)
    {
        UserToken? userToken = null;
        try
        {
            if (receiveArgs.UserToken is not UserToken token) return;

            userToken = token;
            if (receiveArgs.SocketError != SocketError.Success)
            {
                userToken.OnRemoved();
                return;
            }

            if (receiveArgs.Buffer == null) throw new Exception("[ProcessReceive] invalid Buffer");

            if (receiveArgs.BytesTransferred <= 0)
            {
                userToken.OnRemoved();
                return;
            }

            (var errorCode, string? _) =
                userToken.OnReceived(receiveArgs.Buffer, receiveArgs.Offset, receiveArgs.BytesTransferred);
            if (errorCode != ErrorCode.SUCCESS) return;

            // 다음 패킷 수신 대기
            bool willRaiseEvent = userToken.Socket!.ReceiveAsync(receiveArgs);
            if (!willRaiseEvent) ProcessReceive(receiveArgs);
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
