#pragma warning disable CS8618
#pragma warning disable CS8622

using System.Net.Sockets;

namespace network
{
    public class NetworkService
    {
        int connected_count = 0;
        Listener client_listener;
        BufferManager buffer_manager;
        SocketAsyncEventArgsPool recv_event_args_pool;
        SocketAsyncEventArgsPool send_event_args_pool;
        public delegate void sessionHandler(UserToken token);
        public sessionHandler session_created_callback { get; set; }

        public void Initialize()
        {
            this.buffer_manager = new BufferManager(
                Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
                Config.BUFFER_SIZE
            );
            this.recv_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.send_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);

            foreach (var _ in Enumerable.Range(0, Config.MAX_CONNECTION))
            {
                UserToken user_token = new();

                SocketAsyncEventArgs recv_args = new();
                recv_args.Completed += new EventHandler<SocketAsyncEventArgs>(RecvCompleted);
                recv_args.UserToken = user_token;
                this.buffer_manager.SetBuffer(recv_args);
                this.recv_event_args_pool.Push(recv_args);

                SocketAsyncEventArgs send_args = new();
                send_args.Completed += new EventHandler<SocketAsyncEventArgs>(SendCompleted);
                send_args.UserToken = user_token;
                this.buffer_manager.SetBuffer(send_args);
                this.send_event_args_pool.Push(send_args);
            }
        }

        public void Listen(string host, int port, int backlog)
        {
            this.client_listener = new Listener();

            client_listener.onNewClient += OnNewClient;
            client_listener.Start(host, port, backlog);
        }

        // C->S 접속 성공 시 호출
        public void onConnectCompleted(Socket socket, UserToken user_token)
        {
            // 2개만 있으면 되므로 SocketAsyncEventArgsPool 사용 안함
            SocketAsyncEventArgs receive_event_arg = new();
            receive_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(RecvCompleted);
            receive_event_arg.UserToken = user_token;
            receive_event_arg.SetBuffer(new byte[1024], 0, 1024);

            SocketAsyncEventArgs send_event_arg = new();
            send_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(SendCompleted);
            send_event_arg.UserToken = user_token;
            send_event_arg.SetBuffer(new byte[1024], 0, 1024);

            BeginRecv(user_token, socket, receive_event_arg, send_event_arg);
            user_token.heartbeat_timer = new Timer(
                (object _) =>
                {
                    Packet msg = Packet.Create(0);
                    user_token.Send(msg);
                },
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(3)
            );
        }

        void OnNewClient(Socket client_socket, object _)
        {
            try
            {
                Interlocked.Increment(ref this.connected_count);
                Console.WriteLine(
                    $"[{Environment.CurrentManagedThreadId}] A client connected. handle:{client_socket.Handle}, count:{this.connected_count}"
                );

                SocketAsyncEventArgs recv_args = this.recv_event_args_pool.Pop();
                SocketAsyncEventArgs send_args = this.send_event_args_pool.Pop();

                UserToken user_token = GetUserToken(recv_args);
                this.session_created_callback(user_token);

                BeginRecv(user_token, client_socket, recv_args, send_args);
                user_token.heartbeat_timer = new Timer(
                    (object _) =>
                    {
                        if (user_token.is_alive)
                        {
                            user_token.is_alive = false;
                            return;
                        }
                        this.CloseClientSocket(user_token);
                    },
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(10)
                );
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        void BeginRecv(
            UserToken user_token,
            Socket socket,
            SocketAsyncEventArgs recv_args,
            SocketAsyncEventArgs send_args
        )
        {
            user_token.SetEventArgs(recv_args, send_args);
            user_token.socket = socket;

            if (!socket.ReceiveAsync(recv_args))
            {
                ProcessRecv(recv_args);
            }
        }

        void RecvCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.LastOperation != SocketAsyncOperation.Receive)
            {
                throw new ArgumentException(
                    "The last operation completed on the socket was not a receive."
                );
            }

            ProcessRecv(args);
        }

        void SendCompleted(object sender, SocketAsyncEventArgs args)
        {
            UserToken token = GetUserToken(args);
            token.ProcessSend(args);
        }

        void ProcessRecv(SocketAsyncEventArgs recv_args)
        {
            try
            {
                var (user_token, bytes_transferred, socket_error, recv_buffer) = (
                    GetUserToken(recv_args),
                    recv_args.BytesTransferred,
                    recv_args.SocketError,
                    recv_args.Buffer
                );

                if (
                    bytes_transferred <= 0
                    || socket_error != SocketError.Success
                    || recv_args.Buffer == null
                )
                {
                    this.CloseClientSocket(user_token);
                    throw new Exception(
                        $"processRecv fail. BytesTransferred:{recv_args.BytesTransferred}"
                    );
                }

                // 패킷 처리
                user_token.OnReceived(
                    recv_args.Buffer,
                    recv_args.Offset,
                    recv_args.BytesTransferred
                );

                // 다음 패킷 수신 대기
                if (!user_token.socket.ReceiveAsync(recv_args))
                {
                    ProcessRecv(recv_args);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public void CloseClientSocket(UserToken user_token)
        {
            user_token.OnRemoved();
            this.recv_event_args_pool.Push(user_token.recv_event_args);
            this.recv_event_args_pool.Push(user_token.send_event_args);

            Interlocked.Decrement(ref this.connected_count);
        }

        private static UserToken GetUserToken(SocketAsyncEventArgs args)
        {
            if (args.UserToken is not UserToken user_token)
            {
                throw new Exception("user token is null.");
            }

            return user_token;
        }
    }
}
