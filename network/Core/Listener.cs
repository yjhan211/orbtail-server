using System.Net;
using System.Net.Sockets;
using network.common;

namespace network.core
{
    public class Listener
    {
        private readonly SocketAsyncEventArgs _acceptArgs = new();
        private Socket? _listenSocket;
        private AutoResetEvent? _flowControlEvent;
        public delegate void newClientHandler(Socket clientSocket, object? token);
        public event newClientHandler? ClientConnected;
        public void Start(IPAddress address, short port)
        {
            IPEndPoint endPoint = new(address, port);
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(endPoint);
            _listenSocket.Listen(Config.BACK_LOG);

            _acceptArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnAcceptCompleted);

            Thread listen_thread = new(DoListen);
            listen_thread.Start();
        }

        private void DoListen()
        {
            if (_listenSocket == null)
            {
                throw new Exception($"[Listener/DoListen] ListenSocket is null");
            }

            _flowControlEvent = new(false);
            while (true)
            {
                _acceptArgs.AcceptSocket = null;
                if (!_listenSocket.AcceptAsync(_acceptArgs))
                {
                    OnAcceptCompleted(null, _acceptArgs);
                }
                _flowControlEvent.WaitOne();
            }
        }

        private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs socketEventArgs)
        {
            if (_flowControlEvent == null)
            {
                throw new Exception($"[Listener/OnAcceptCompleted] FlowControllEvent is null");
            }

            if (socketEventArgs.SocketError != SocketError.Success)
            {
                throw new Exception($"[Listener/OnAcceptCompleted] SocketError:{socketEventArgs.SocketError}");
            }

            if (socketEventArgs.AcceptSocket == null)
            {
                throw new Exception($"[Listener/OnAcceptCompleted] AcceptSocket is null");
            }

            if (ClientConnected == null)
            {
                throw new Exception($"[Listener/OnAcceptCompleted] ClientConnected is null");
            }

            ClientConnected(socketEventArgs.AcceptSocket, null);
            _flowControlEvent.Set();
        }
    }
}
