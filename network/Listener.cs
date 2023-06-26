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

        public delegate void newClientHandler(Socket client_socket, object token);

        public newClientHandler onNewClient;

        public void Start(string host, int port, int backlog)
        {
            try
            {
                IPAddress address = host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(host);
                IPEndPoint end_point = new(address, port);

                this.listen_socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp
                );
                this.listen_socket.Bind(end_point);
                this.listen_socket.Listen(backlog);

                this.accept_args = new();
                this.accept_args.Completed += new EventHandler<SocketAsyncEventArgs>(
                    OnAcceptCompleted
                );

                Thread listen_thread = new(DoListen);
                listen_thread.Start();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        void DoListen()
        {
            this.flow_control_event = new AutoResetEvent(false);

            while (true)
            {
                this.accept_args.AcceptSocket = null;
                try
                {
                    if (!listen_socket.AcceptAsync(this.accept_args))
                    {
                        OnAcceptCompleted(null, this.accept_args);
                    }

                    this.flow_control_event.WaitOne();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"doListen Fail. {e.Message}");
                    continue;
                }
            }
        }

        void OnAcceptCompleted(object? sender, SocketAsyncEventArgs socket_event_args)
        {
            var (socket_error, accept_socket, user_token) = (
                socket_event_args.SocketError,
                socket_event_args.AcceptSocket,
                socket_event_args.UserToken
            );

            this.onNewClient(accept_socket, user_token);
            this.flow_control_event.Set();
        }
    }
}
