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

        UserToken getUserToken(SocketAsyncEventArgs args)
        {
            if (args.UserToken is not UserToken user_token)
            {
                throw new Exception("user token is null.");
            }

            return user_token;
        }

        public void initialize()
        {
            this.buffer_manager = new BufferManager(Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE, Config.BUFFER_SIZE);
            this.recv_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.send_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);

            foreach (var _ in Enumerable.Range(0, Config.MAX_CONNECTION))
            {
                UserToken user_token = new UserToken();

                SocketAsyncEventArgs recv_args = new SocketAsyncEventArgs();
                recv_args.Completed += new EventHandler<SocketAsyncEventArgs>(recvCompleted);
                recv_args.UserToken = user_token;
                this.buffer_manager.SetBuffer(recv_args);
                this.recv_event_args_pool.Push(recv_args);

                SocketAsyncEventArgs send_args = new SocketAsyncEventArgs();
                send_args.Completed += new EventHandler<SocketAsyncEventArgs>(sendCompleted);
                send_args.UserToken = user_token;
                this.buffer_manager.SetBuffer(send_args);
                this.send_event_args_pool.Push(send_args);
            }
        }

        public void listen(string host, int port, int backlog)
        {
            this.client_listener = new Listener();

            client_listener.onNewClient += onNewClient;
            client_listener.start(host, port, backlog);
        }

        // C->S 접속 성공 시 호출
        public void onConnectCompleted(Socket socket, UserToken user_token)
        {
            // 2개만 있으면 되므로 SocketAsyncEventArgsPool 사용 안함
            SocketAsyncEventArgs receive_event_arg = new SocketAsyncEventArgs();
            receive_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(recvCompleted);
            receive_event_arg.UserToken = user_token;
            receive_event_arg.SetBuffer(new byte[1024], 0, 1024);

            SocketAsyncEventArgs send_event_arg = new SocketAsyncEventArgs();
            send_event_arg.Completed += new EventHandler<SocketAsyncEventArgs>(sendCompleted);
            send_event_arg.UserToken = user_token;
            send_event_arg.SetBuffer(new byte[1024], 0, 1024);

            beginRecv(user_token, socket, receive_event_arg, send_event_arg);
        }

        void onNewClient(Socket client_socket, object token)
        {
            try
            {
                Interlocked.Increment(ref this.connected_count);
                Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] A client connected. handle:{client_socket.Handle}, count:{this.connected_count}");

                SocketAsyncEventArgs recv_args = this.recv_event_args_pool.Pop();
                SocketAsyncEventArgs send_args = this.send_event_args_pool.Pop();

                UserToken user_token = getUserToken(recv_args);
                this.session_created_callback(user_token);

                beginRecv(user_token, client_socket, recv_args, send_args);
                user_token.startHeartBeat();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        void beginRecv(UserToken user_token, Socket socket, SocketAsyncEventArgs recv_args, SocketAsyncEventArgs send_args)
        {
            user_token.setEventArgs(recv_args, send_args);
            user_token.socket = socket;

            if (!socket.ReceiveAsync(recv_args))
            {
                processRecv(recv_args);
            }
        }

        void recvCompleted(object sender, SocketAsyncEventArgs args)
        {
            if (args.LastOperation != SocketAsyncOperation.Receive)
            {
                throw new ArgumentException("The last operation completed on the socket was not a receive.");
            }

            processRecv(args);
        }

        void sendCompleted(object sender, SocketAsyncEventArgs args)
        {
            UserToken token = getUserToken(args);
            token.processSend(args);
        }

        void processRecv(SocketAsyncEventArgs recv_args)
        {
            try
            {
                var (user_token, bytes_transferred, socket_error) = (getUserToken(recv_args), recv_args.BytesTransferred, recv_args.SocketError);
                if (bytes_transferred <= 0 || socket_error != SocketError.Success)
                {
                    // TODO close_clientsocket
                    throw new Exception($"processRecv fail. BytesTransferred:{recv_args.BytesTransferred}");
                }

                // 패킷 처리
                user_token.onReceived(recv_args.Buffer, recv_args.Offset, recv_args.BytesTransferred);

                // 다음 패킷 수신 대기
                if (!user_token.socket.ReceiveAsync(recv_args))
                {
                    processRecv(recv_args);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public void closeClientSocket(UserToken token)
        {
            token.onRemoved();
            this.recv_event_args_pool.Push(token.recv_event_args);
            this.recv_event_args_pool.Push(token.send_event_args);
        }
    }
}
