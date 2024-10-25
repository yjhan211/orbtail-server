using System.Net;
using System.Net.Sockets;
using network.managers;
using network.common;

namespace network.core
{
    public class NetworkService
    {
        private readonly LogManager _logManager;
        private readonly BufferManager _bufferManager;
        private readonly SocketAsyncEventArgsManager _recvEventArgsManager;
        private readonly SocketAsyncEventArgsManager _sendEventArgsManager;
        private readonly object _initEventArgsLock = new();
        private readonly Listener _clientListener = new();
        public Action<UserToken>? SessionCreatedCallback { get; set; }

        public NetworkService(LogManager logManager)
        {
            _logManager = logManager;
            _bufferManager = new(Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE, Config.BUFFER_SIZE);
            _recvEventArgsManager = new(Config.MAX_CONNECTION);
            _sendEventArgsManager = new(Config.MAX_CONNECTION);

            InitializeEventArgs();
        }

        private void InitializeEventArgs()
        {
            for (int i = 0; i < Config.MAX_CONNECTION; i++)
            {
                var userToken = new UserToken(i, _logManager);

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

        // from Listener
        private void OnNewClient(Socket clientSocket, object _)
        {
            try
            {
                SocketAsyncEventArgs recvArgs;
                SocketAsyncEventArgs sendArgs;

                lock (_initEventArgsLock)
                {
                    recvArgs = _recvEventArgsManager.Pop();
                    sendArgs = _sendEventArgsManager.Pop();
                }

                var argsToken = recvArgs.UserToken;
                if (argsToken == null)
                {
                    throw new Exception("[OnNewClient] Invalid UserToken");
                }


                var userToken = (UserToken)argsToken;
                SessionCreatedCallback?.Invoke(userToken);
                BeginRecv(userToken, clientSocket, recvArgs, sendArgs);
            }
            catch (Exception e)
            {
                _logManager.WriteErrorLog(e);
            }
        }

        private void BeginRecv(UserToken userToken, Socket socket, SocketAsyncEventArgs recvArgs, SocketAsyncEventArgs sendArgs)
        {
            userToken.SetEventArgs(recvArgs, sendArgs);
            userToken.Socket = socket;

            bool willRaiseEvent = socket.ReceiveAsync(recvArgs);
            if (!willRaiseEvent)
            {
                ProcessRecv(recvArgs);
            }
        }

        private void RecvCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.LastOperation != SocketAsyncOperation.Receive)
            {
                throw new ArgumentException("The last operation completed on the socket was not a receive.");
            }

            ProcessRecv(args);
        }

        private void ProcessRecv(SocketAsyncEventArgs recvArgs)
        {
            UserToken? userToken = null;
            try
            {
                var argsToken = recvArgs.UserToken;
                if (argsToken == null)
                {
                    throw new Exception("[ProcessRecv] invalid userToken");
                }

                userToken = (UserToken)argsToken;
                if (recvArgs.SocketError != SocketError.Success)
                {
                    userToken?.OnRemoved();
                    return;
                }

                if (recvArgs.Buffer == null)
                {
                    throw new Exception("[ProcessRecv] invalid Buffer");
                }

                if (recvArgs.BytesTransferred > 0)
                {
                    (ErrorCode errorCode, string? errorLog) = userToken.OnReceived(recvArgs.Buffer, recvArgs.Offset, recvArgs.BytesTransferred);
                    if (errorCode != ErrorCode.SUCCESS)
                    {
                        _logManager.WriteErrorLog(new Exception($"errorCode:{errorCode}, errorLog:{errorLog}"));
                    }
                }
                else if (recvArgs.BytesTransferred == 0)
                {
                    // 연결이 종료되었을 수 있음
                    userToken?.OnRemoved();
                    return;
                }

                // 다음 패킷 수신 대기
                bool willRaiseEvent = userToken.Socket!.ReceiveAsync(recvArgs);
                if (!willRaiseEvent)
                {
                    ProcessRecv(recvArgs);
                }
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
                userToken?.OnRemoved();
            }
        }

        private void SendCompleted(object sender, SocketAsyncEventArgs send_args)
        {
            try
            {
                if (send_args.UserToken is not UserToken token)
                {
                    throw new Exception($"invalid args.UserToken : {send_args.UserToken}");
                }
                token.ProcessSend(send_args);
            }
            catch (Exception e)
            {
                _logManager.WriteErrorLog(e);
            }
        }

        public void CloseClientSocket(UserToken? userToken)
        {
            if (userToken == null || userToken.IsReleased)
            {
                return;
            }

            try
            {
                userToken.Disconnect();

                lock (_initEventArgsLock)
                {
                    _recvEventArgsManager.Push(userToken.RecvEventArgs!);
                    _sendEventArgsManager.Push(userToken.SendEventArgs!);
                }
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
            }
        }
    }
}
