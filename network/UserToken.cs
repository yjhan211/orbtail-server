#pragma warning disable CS8604
#pragma warning disable CS8622

using System.Net.Sockets;
using static System.Net.Mime.MediaTypeNames;

namespace network
{
    public class UserToken
    {
        public Socket socket { get; set; }
        public SocketAsyncEventArgs recv_event_args { get; private set; }
        public SocketAsyncEventArgs send_event_args { get; private set; }

        MessageResolver message_resolver;

        IPeer peer;

        Queue<Packet> sending_queue;
        private object cs_sending_queue;

        Timer heartbeat_timer;

        public UserToken()
        {
            this.cs_sending_queue = new object();
            this.message_resolver = new MessageResolver();
            this.sending_queue = new Queue<Packet>();
        }

        public void setPeer(IPeer peer)
        {
            this.peer = peer;
        }

        public void setEventArgs(SocketAsyncEventArgs receive_event_args, SocketAsyncEventArgs send_event_args)
        {
            this.recv_event_args = receive_event_args;
            this.send_event_args = send_event_args;
        }

        public void onReceived(byte[] buffer, int offset, int transfered)
        {
            this.message_resolver.onReceived(buffer, offset, transfered, onMessage);
        }

        void onMessage(Const<byte[]> buffer)
        {
            if (this.peer is null)
            {
                return;
            }

            this.peer.onMessage(buffer);
        }

        public void onRemoved()
        {
            this.sending_queue.Clear();
            if (this.peer is not null)
            {
                this.peer.onRemoved();
            }
        }

        public void send(Packet msg)
        {
            Packet clone = new Packet();
            msg.copyTo(clone);

            lock (this.cs_sending_queue)
            {
                bool is_sending = this.sending_queue.Count <= 0;
                this.sending_queue.Enqueue(clone);
                if (!is_sending)
                {
                    // 현재 전송중이지 않으므로 전송 시작
                    startSend();
                }
            }
        }

        void startSend()
        {
            lock (this.cs_sending_queue)
            {
                Packet msg = this.sending_queue.Peek();
                msg.recordSize(); // 헤더에 패킷 사이즈 기록

                this.send_event_args.SetBuffer(this.send_event_args.Offset, msg.position);
                Array.Copy(msg.buffer, 0, this.send_event_args.Buffer, this.send_event_args.Offset, msg.position);
                if (!this.socket.SendAsync(this.send_event_args))
                {
                    processSend(this.send_event_args);
                }
            }
        }

        static int sent_count = 0;

        public void processSend(SocketAsyncEventArgs args)
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

                Interlocked.Increment(ref sent_count);
                Console.WriteLine(string.Format($"[{Thread.CurrentThread.ManagedThreadId}] [send] {args.SocketError} | transferred: {args.BytesTransferred}, {sent_count}"));

                this.sending_queue.Dequeue();
                if (this.sending_queue.Count > 0)
                {
                    startSend();
                }
            }
        }

        public void disconnect()
        {
            try
            {
                this.socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) { }
            this.socket.Close();
        }

        public void startHeartBeat()
        {
            TimerCallback callback = new(sendHeartBeat);
            this.heartbeat_timer = new Timer(callback, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }

        private void sendHeartBeat(object state)
        {
            Packet msg = Packet.create(0);
            msg.push(0);
            send(msg);
        }
    }
}
