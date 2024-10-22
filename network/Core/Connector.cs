using System.Net;
using System.Net.Sockets;
using network.managers;

namespace network.core
{
    public class Connector
    {
        private readonly NetworkService _networkService;
        private readonly LogManager _logManager;
        private Socket? _client;
        public delegate void ConnectEventHandler(UserToken token);
        public event ConnectEventHandler? Connected;

        public Connector(NetworkService networkService, LogManager logManager)
        {
            _networkService = networkService;
            _logManager = logManager;
        }

        public void Connect(IPEndPoint remoteEndpoint)
        {
            SocketAsyncEventArgs eventArgs = new();
            eventArgs.Completed += OnConnectCompleted;
            eventArgs.RemoteEndPoint = remoteEndpoint;

            _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (!_client.ConnectAsync(eventArgs))
            {
                OnConnectCompleted(null, eventArgs);
            }
        }

        private void OnConnectCompleted(object? sender, SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success || _client == null || Connected == null)
            {
                throw new Exception($"[Connector/OnCommectCompleted] {args.SocketError}");
            }

            var token = new UserToken(1, _logManager);
            _networkService.OnConnectCompleted(_client, token);
            Connected(token);
        }
    }
}
