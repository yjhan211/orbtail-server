#pragma warning disable CS8604
#pragma warning disable CS8618
#pragma warning disable CS8622

using System.Net;
using System.Net.Sockets;

namespace network
{
    class Listener
    {
        SocketAsyncEventArgs accept_args;

        Socket listen_socket;

        AutoResetEvent flow_control_event;

        public delegate void newClientHandler(Socket client_socket, object? token);

        public newClientHandler onNewClient;

        public void Start()
        {
            try
            {
                IPAddress address =
                    Config.IP == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(Config.IP);

                IPEndPoint end_point = new(address, Config.PORT);

                this.listen_socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp
                );

                this.listen_socket.Bind(end_point);
                this.listen_socket.Listen(Config.BACK_LOG);

                this.accept_args = new();
                this.accept_args.Completed += new EventHandler<SocketAsyncEventArgs>(
                    OnAcceptCompleted
                );

                Thread listen_thread = new(DoListen);
                listen_thread.Start();
            }
            catch (Exception e)
            {
                // TODO 파일로깅
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
            }
        }

        void DoListen()
        {
            this.flow_control_event = new AutoResetEvent(false);

            while (true)
            {
                this.accept_args.AcceptSocket = null;
                if (!listen_socket.AcceptAsync(this.accept_args))
                {
                    OnAcceptCompleted(null, this.accept_args);
                }
                this.flow_control_event.WaitOne();
            }
        }

        void OnAcceptCompleted(object? sender, SocketAsyncEventArgs socket_event_args)
        {
            if (socket_event_args.SocketError != SocketError.Success)
            {
                throw new Exception($"AcceptFail. SocketError:{socket_event_args.SocketError}");
            }

            if (socket_event_args.AcceptSocket == null)
            {
                throw new Exception($"AcceptFail. AcceptSocket is null");
            }

            this.onNewClient(socket_event_args.AcceptSocket, null);
            this.flow_control_event.Set();
        }
    }
}
