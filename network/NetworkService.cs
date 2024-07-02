#pragma warning disable CS8618
#pragma warning disable CS8622
#pragma warning disable CS8600
#pragma warning disable CS8604

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace network
{
    public class NetworkService
    {
        Listener client_listener;
        BufferManager buffer_manager;
        SocketAsyncEventArgsPool recv_event_args_pool;
        SocketAsyncEventArgsPool send_event_args_pool;
        ConnectionManager connection_manager;
        public delegate void sessionHandler(UserToken token);
        public sessionHandler session_created_callback { get; set; }
        object init_event_args_lock;
        ConcurrentDictionary<string, UserToken> active_connections = new();

        public void Initialize()
        {
            this.buffer_manager = new BufferManager(
                Config.MAX_CONNECTION * Config.PRE_ALLOC_COUNT * Config.BUFFER_SIZE,
                Config.BUFFER_SIZE
            );
            this.recv_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.send_event_args_pool = new SocketAsyncEventArgsPool(Config.MAX_CONNECTION);
            this.init_event_args_lock = new();

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

            this.connection_manager = new ConnectionManager();
            this.active_connections = new();

            // 주기적으로 연결 정리를 수행하는 타이머 설정
            Timer cleanup_timer = new Timer(
                _ => CleanupConnections(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(1)
            );
        }

        public void Listen(IPAddress address, short port)
        {
            this.client_listener = new();
            this.client_listener.onNewClient += OnNewClient;
            this.client_listener.Start(address, port);
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
                    Packet msg = Packet.Create((int)PROTOCOL.C_TO_U_HEART_BEAT, 0);
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
                string ip_address = ((IPEndPoint)client_socket.RemoteEndPoint!).Address.ToString();
                // if (!this.connection_manager.CanAcceptConnection(ip_address))
                // {
                //     LogManager.WriteDebugLog($"Connection limit exceeded for IP: {ip_address}");
                //     client_socket.Close();
                //     return;
                // }

                LogManager.WriteDebugLog($"on new client from {ip_address}");

                SocketAsyncEventArgs recv_args = null;
                SocketAsyncEventArgs send_args = null;

                lock (this.init_event_args_lock)
                {
                    recv_args = this.recv_event_args_pool.Pop();
                    send_args = this.send_event_args_pool.Pop();
                }

                UserToken user_token = (UserToken)recv_args.UserToken;
                user_token!.ip_address = ip_address;
                this.connection_manager.AddConnection(ip_address, user_token);
                this.active_connections[ip_address] = user_token;

                this.session_created_callback(user_token);

                BeginRecv(user_token, client_socket, recv_args, send_args);

                if (Config.HEARTBEAT_ACTIVE)
                {
                    SetupHeartbeat(user_token);
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        void SetupHeartbeat(UserToken user_token)
        {
            user_token.heartbeat_timer = new Timer(
                _ =>
                {
                    if (user_token.is_alive)
                    {
                        user_token.is_alive = false;
                        return;
                    }
                    CloseClientSocket(user_token);
                },
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10)
            );
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
            UserToken user_token = (UserToken)recv_args.UserToken;
            try
            {
                if (recv_args.SocketError != SocketError.Success || recv_args.BytesTransferred <= 0)
                {
                    CloseClientSocket(user_token);
                    return;
                }

                user_token!.UpdateActivityTime();

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
                LogManager.WriteErrorLog(e);
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
            lock (user_token.lock_disconnect)
            {
                if (user_token.is_released)
                {
                    return;
                }

                try
                {
                    user_token.is_released = true;

                    if (user_token.socket.Connected)
                    {
                        user_token.socket.Shutdown(SocketShutdown.Both);
                    }

                    user_token.socket.Close();
                    user_token.OnRemoved();

                    this.connection_manager.RemoveConnection(user_token.ip_address, user_token);
                    this.active_connections.TryRemove(user_token.ip_address, out _);

                    lock (this.init_event_args_lock)
                    {
                        this.recv_event_args_pool.Push(user_token.recv_event_args);
                        this.send_event_args_pool.Push(user_token.send_event_args);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.WriteErrorLog(ex);
                }
            }
        }

        void CleanupConnections()
        {
            var now = DateTime.Now;
            foreach (var kvp in this.active_connections)
            {
                var user_token = kvp.Value;
                if (
                    (now - user_token.last_activity_time).TotalSeconds
                    > Config.CONNECTION_TIMEOUT_SECONDS
                )
                {
                    CloseClientSocket(user_token);
                }
            }
        }
    }
}
