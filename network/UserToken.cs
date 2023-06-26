#pragma warning disable CS8604
#pragma warning disable CS8622
#pragma warning disable CS8618

using System.Net.Sockets;

namespace network
{
    public class UserToken
    {
        public Socket socket { get; set; }
        public SocketAsyncEventArgs recv_event_args { get; private set; }
        public SocketAsyncEventArgs send_event_args { get; private set; }
        readonly MessageResolver message_resolver;
        IPeer peer;
        readonly Queue<Packet> sending_queue;
        readonly object cs_sending_queue;
        public Timer heartbeat_timer;
        public bool is_alive = true;
        public bool is_released = true;
        public object lock_disconnect;

        public UserToken()
        {
            this.cs_sending_queue = new();
            this.message_resolver = new();
            this.sending_queue = new();
            this.lock_disconnect = new();
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

        public void OnReceived(byte[] buffer, int offset, int transfered)
        {
            this.message_resolver.OnReceived(buffer, offset, transfered, OnMessage);
        }

        void OnMessage(Const<byte[]> buffer)
        {
            this.peer?.OnMessage(buffer);
        }

        public void OnRemoved()
        {
            this.sending_queue.Clear();
            this.peer?.OnRemoved();
            this.heartbeat_timer?.Dispose();
        }

        public void Send(Packet msg)
        {
            Packet clone = new();
            msg.CopyTo(clone);

            lock (this.cs_sending_queue)
            {
                bool is_sending = this.sending_queue.Count > 0;
                this.sending_queue.Enqueue(clone);
                if (!is_sending)
                {
                    // 현재 전송중이지 않으므로 전송 시작
                    StartSend();
                }
            }
        }

        void StartSend()
        {
            lock (this.cs_sending_queue)
            {
                Packet msg = this.sending_queue.Peek();
                msg.RecordSize(); // 헤더에 패킷 사이즈 기록

                this.send_event_args.SetBuffer(this.send_event_args.Offset, msg.position);
                Array.Copy(
                    msg.buffer,
                    0,
                    this.send_event_args.Buffer,
                    this.send_event_args.Offset,
                    msg.position
                );

                if (!this.socket.SendAsync(this.send_event_args))
                {
                    ProcessSend(this.send_event_args);
                }
            }
        }

        public void ProcessSend(SocketAsyncEventArgs args)
        {
            if (args.BytesTransferred <= 0 || args.SocketError != SocketError.Success)
            {
                return;
            }

            lock (this.cs_sending_queue)
            {
                if (this.sending_queue.Count <= 0)
                {
                    return;
                }

                // TODO 테스트 해야됨
                // TODO 패킷 하나를 다 못보낸 경우 처리
                int size = this.sending_queue.Peek().position;
                if (args.BytesTransferred != size)
                {
                    return;
                }

                Console.WriteLine(
                    $"[{Environment.CurrentManagedThreadId}] [send] {args.SocketError} | transferred: {args.BytesTransferred}"
                );

                this.sending_queue.Dequeue();
                if (this.sending_queue.Count > 0)
                {
                    StartSend();
                }
            }
        }

        public void Disconnect()
        {
            this.socket.Shutdown(SocketShutdown.Send);
            this.socket.Close();
        }
    }
}
