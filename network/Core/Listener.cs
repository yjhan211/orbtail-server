using System.Net;
using System.Net.Sockets;
using network.common;

namespace network.core;

public class Listener
{
    public delegate void NewClientHandler(Socket clientSocket, object? token);

    private readonly SocketAsyncEventArgs _acceptArgs = new();
    private readonly CancellationTokenSource _cts = new();
    private AutoResetEvent? _flowControlEvent;
    private Socket? _listenSocket;
    public event NewClientHandler? ClientConnected;

    public void Start(IPAddress address, short port)
    {
        IPEndPoint endPoint = new(address, port);
        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.Bind(endPoint);
        _listenSocket.Listen(Config.BACK_LOG);

        _acceptArgs.Completed += OnAcceptCompleted;

        var listenThread = new Thread(DoListen) { IsBackground = true };
        listenThread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _listenSocket?.Close();
        _flowControlEvent?.Set();
    }

    private void DoListen()
    {
        if (_listenSocket == null) throw new Exception("[Listener/DoListen] ListenSocket is null");

        _flowControlEvent = new AutoResetEvent(false);
        while (!_cts.IsCancellationRequested)
        {
            _acceptArgs.AcceptSocket = null;

            try
            {
                if (!_listenSocket.AcceptAsync(_acceptArgs))
                {
                    OnAcceptCompleted(null, _acceptArgs);
                }

                _flowControlEvent.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                // Stop()으로 소켓이 닫힌 경우 — 정상 종료
                break;
            }
        }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs socketEventArgs)
    {
        if (_flowControlEvent == null) throw new Exception("[Listener/OnAcceptCompleted] FlowControlEvent is null");

        if (socketEventArgs.SocketError != SocketError.Success)
        {
            // 종료 중이면 예외 대신 무시
            if (_cts.IsCancellationRequested)
            {
                _flowControlEvent.Set();
                return;
            }

            throw new Exception($"[Listener/OnAcceptCompleted] SocketError:{socketEventArgs.SocketError}");
        }

        if (socketEventArgs.AcceptSocket == null)
            throw new Exception("[Listener/OnAcceptCompleted] AcceptSocket is null");

        if (ClientConnected == null) throw new Exception("[Listener/OnAcceptCompleted] ClientConnected is null");

        ClientConnected(socketEventArgs.AcceptSocket, null);
        _flowControlEvent.Set();
    }
}
