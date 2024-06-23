#pragma warning disable CS8604
#pragma warning disable CS8618

using System.Net;
using System.Net.Sockets;

namespace network
{
    public class UserToken
    {
        public Socket socket { get; set; }
        public SocketAsyncEventArgs recv_event_args { get; private set; }
        public SocketAsyncEventArgs send_event_args { get; private set; }
        readonly MessageResolver message_resolver;
        public IPeer peer;
        readonly Queue<Packet> sending_queue;
        readonly object lock_sending_queue;
        public Timer heartbeat_timer;
        public bool is_alive = true;
        public bool is_released = true;
        public object lock_disconnect;
        public NetworkService network_service;
        public string ip_address { get; set; }
        public DateTime last_activity_time { get; private set; }

        public UserToken(NetworkService network_service)
        {
            this.lock_sending_queue = new();
            this.message_resolver = new();
            this.sending_queue = new();
            this.lock_disconnect = new();
            this.network_service = network_service;
            this.last_activity_time = DateTime.UtcNow;
        }

        public void SetPeer(IPeer peer)
        {
            this.peer = peer;
        }

        public void SetEventArgs(
            SocketAsyncEventArgs receive_event_args,
            SocketAsyncEventArgs send_event_args
        )
        {
            this.recv_event_args = receive_event_args;
            this.send_event_args = send_event_args;
        }

        public (ErrorCode error_code, string? error_log) OnReceived(
            byte[] buffer,
            int offset,
            int transfered
        )
        {
            return this.message_resolver.OnReceived(buffer, offset, transfered, OnMessage);
        }

        void OnMessage(Const<byte[]> buffer)
        {
            this.peer?.OnMessageFromClient(buffer);
        }

        public void Send(Packet msg)
        {
            Packet clone = new();
            msg.CopyTo(clone);

            lock (this.lock_sending_queue)
            {
                if (this.is_released)
                {
                    return;
                }

                bool is_sending = this.sending_queue.Count > 0;
                this.sending_queue.Enqueue(clone);

                if (!is_sending)
                {
                    StartSend();
                }
            }
        }

        void StartSend()
        {
            try
            {
                lock (this.lock_sending_queue)
                {
                    Packet packet = this.sending_queue.Peek();
                    packet.RecordSize();

                    this.send_event_args.SetBuffer(this.send_event_args.Offset, packet.position);
                    Array.Copy(
                        packet.buffer,
                        0,
                        this.send_event_args.Buffer,
                        this.send_event_args.Offset,
                        packet.position
                    );

                    if (!this.socket.SendAsync(this.send_event_args))
                    {
                        ProcessSend(this.send_event_args);
                    }
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        public void ProcessSend(SocketAsyncEventArgs send_args)
        {
            if (send_args.SocketError != SocketError.Success || send_args.BytesTransferred <= 0)
            {
                throw new Exception(
                    $"send_args.SocketError not Success. SocketError:{send_args.SocketError}, bytesTransferred:{send_args.BytesTransferred}"
                );
            }

            lock (this.lock_sending_queue)
            {
                // 보낼 것이 없음
                if (this.sending_queue.Count <= 0)
                {
                    return;
                }

                // 전송 완료
                if (this.sending_queue.Sum(buffer => buffer.position) <= send_args.BytesTransferred)
                {
                    this.sending_queue.Clear();
                    return;
                }

                int sum = 0;
                while (true)
                {
                    sum += this.sending_queue.Peek().position;
                    if (sum <= send_args.BytesTransferred)
                    {
                        // 이미 보낸 패킷이므로 제거
                        this.sending_queue.Dequeue();
                        continue;
                    }
                    break;
                }

                StartSend();
            }
        }

        public void OnRemoved()
        {
            lock (this.lock_sending_queue)
            {
                this.is_released = true;
                this.sending_queue.Clear();
            }

            try
            {
                this.peer?.OnRemoved();

                if (this.heartbeat_timer != null)
                {
                    this.heartbeat_timer.Dispose();
                }
            }
            catch (Exception ex)
            {
                // 예외 처리 및 로깅
                Console.WriteLine($"Error in OnRemoved: {ex.Message}");
            }
        }

        public void UpdateActivityTime()
        {
            this.last_activity_time = DateTime.Now;
        }
    }
}
