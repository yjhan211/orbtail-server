#pragma warning disable CS8618
#pragma warning disable CS8622
#pragma warning disable CS8600
#pragma warning disable CS8604

using System.Net.Sockets;

namespace network
{
    public class NetworkService
    {
        Listener client_listener;
        BufferManager buffer_manager;
        SocketAsyncEventArgsPool recv_event_args_pool;
        SocketAsyncEventArgsPool send_event_args_pool;
        public delegate void sessionHandler(UserToken token);
        public sessionHandler session_created_callback { get; set; }
        object init_event_args_lock;

        public void Initialize()
        {
            this.buffer_manager = new BufferManager(
                Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
                Config.BUFFER_SIZE
            );
            this.recv_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.send_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.init_event_args_lock = new Object();

            foreach (var _ in Enumerable.Range(0, Config.MAX_CONNECTION))
            {
                UserToken user_token = new(this);

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

        public void Listen()
        {
            this.client_listener = new Listener();

            this.client_listener.onNewClient += OnNewClient;
            this.client_listener.Start();
        }

        // Connector->OnConnectCompleted()
        public void OnConnectCompleted(Socket socket, UserToken user_token)
        {
            SocketAsyncEventArgs receive_event_arg = new();
            receive_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(RecvCompleted);
            receive_event_arg.UserToken = user_token;
            receive_event_arg.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

            SocketAsyncEventArgs send_event_arg = new();
            send_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(SendCompleted);
            send_event_arg.UserToken = user_token;
            send_event_arg.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);

            BeginRecv(user_token, socket, receive_event_arg, send_event_arg);

            user_token.heartbeat_timer = new Timer(
                (object _) =>
                {
                    Packet msg = Packet.Create(PROTOCOL.HEART_BEAT);
                    user_token.Send(msg);
                },
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(3)
            );
        }

        // Listener->sessionCreatedCallback()
        void OnNewClient(Socket client_socket, object _)
        {
            try
            {
                Console.WriteLine("on new client");
                SocketAsyncEventArgs recv_args = null;
                SocketAsyncEventArgs send_args = null;

                lock (this.init_event_args_lock)
                {
                    recv_args = this.recv_event_args_pool.Pop();
                    send_args = this.send_event_args_pool.Pop();
                }

                if (
                    recv_args.UserToken is not UserToken
                    || send_args.UserToken is not UserToken
                    || recv_args.UserToken != send_args.UserToken
                )
                {
                    throw new Exception(
                        $"invalid args.UserToken: {recv_args.UserToken}|{send_args.UserToken}"
                    );
                }

                UserToken user_token = (UserToken)recv_args.UserToken;
                this.session_created_callback(user_token);

                BeginRecv(user_token, client_socket, recv_args, send_args);

                // user_token.heartbeat_timer = new Timer(
                //     (object _) =>
                //     {
                //         if (user_token.is_alive)
                //         {
                //             user_token.is_alive = false;
                //             return;
                //         }
                //         this.CloseClientSocket(user_token);
                //     },
                //     null,
                //     TimeSpan.Zero,
                //     TimeSpan.FromSeconds(10)
                // );
            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
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

        void ProcessRecv(SocketAsyncEventArgs recv_args)
        {
            UserToken user_token = null;
            try
            {
                if (recv_args.UserToken is not UserToken)
                {
                    throw new Exception($"invalid args.UserToken : {recv_args.UserToken}");
                }

                // UserToken 획득
                user_token = (UserToken)recv_args.UserToken;

                if (recv_args.SocketError != SocketError.Success)
                {
                    throw new Exception($"recv_args.SocketError is not Success.");
                }

                // 버퍼 확인
                if (recv_args.BytesTransferred <= 0)
                {
                    throw new Exception("recv_args.BytesTransferred less then 0");
                }

                if (recv_args.Buffer == null)
                {
                    throw new Exception($"recv_args.Buffer is null.");
                }

                // 패킷 처리
                (ErrorCode error_code, string? error_log) = user_token.OnReceived(
                    recv_args.Buffer,
                    recv_args.Offset,
                    recv_args.BytesTransferred
                );

                if (error_code != ErrorCode.SUCCESS)
                {
                    throw new Exception(error_log);
                }

                // 다음 패킷 수신 대기
                if (!user_token.socket.ReceiveAsync(recv_args))
                {
                    ProcessRecv(recv_args);
                }
            }
            catch (Exception e)
            {
                // TODO 파일로깅
                Console.WriteLine($"{e.Message}, {e.StackTrace}");
                this.CloseClientSocket(user_token);
            }
        }

        void SendCompleted(object sender, SocketAsyncEventArgs send_args)
        {
            if (send_args.UserToken is not UserToken token)
            {
                throw new Exception($"invalid args.UserToken : {send_args.UserToken}");
            }

            token.ProcessSend(send_args);
        }

        public void CloseClientSocket(UserToken user_token)
        {
            lock (this.init_event_args_lock)
            {
                lock (user_token.lock_disconnect)
                {
                    if (!user_token.is_released)
                    {
                        user_token.OnRemoved();

                        this.recv_event_args_pool.Push(user_token.recv_event_args);
                        this.send_event_args_pool.Push(user_token.send_event_args);
                    }
                }
            }
        }
    }
}
