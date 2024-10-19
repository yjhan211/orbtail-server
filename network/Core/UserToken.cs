using System.Net.Sockets;
using network.interfaces;
using network.common;
using network.packets;
using network.utils;
using network.managers;

namespace network.core
{
    public class UserToken
    {
        private LogManager _logManager;
        public int TokenId { get; }
        private readonly MessageResolver _messageResolver;
        private readonly Queue<Packet> _sendingQueue;
        private readonly object _lockSendingQueue;
        public object _lockDisconnect;
        private IPeer? _peer;
        private Timer? _heartbeatTimer;
        public SocketAsyncEventArgs? RecvEventArgs { get; private set; }
        public SocketAsyncEventArgs? SendEventArgs { get; private set; }
        public Socket? Socket { get; set; }
        public bool IsAlive { get; set; } = true;
        public bool IsReleased { get; private set; } = false;

        public event Action<UserToken>? Disconnected;

        public UserToken(int tokenId, LogManager logManager)
        {
            _logManager = logManager;
            TokenId = tokenId;
            _messageResolver = new();
            _sendingQueue = new();
            _lockSendingQueue = new();
            _lockDisconnect = new();
        }

        public void SetPeer(IPeer peer)
        {
            _peer = peer;
            IsReleased = false;
        }
        public void SetHeartbeatTimer(LogManager logManager)
        {
            _heartbeatTimer = new Timer((_) =>
                {
                    try
                    {
                        var msg = Packet.Create((int)PROTOCOL.C_TO_U_HEART_BEAT, 0);
                        Send(msg);
                    }
                    catch (Exception ex)
                    {
                        logManager.WriteErrorLog(ex);
                    }
                },
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(3)
            );
        }
        public void SetEventArgs(SocketAsyncEventArgs receiveEventArgs, SocketAsyncEventArgs sendEventArgs)
        {
            RecvEventArgs = receiveEventArgs;
            SendEventArgs = sendEventArgs;
        }

        public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transfered)
        {
            return _messageResolver.OnReceived(buffer, offset, transfered, OnMessage);
        }

        private void OnMessage(Const<byte[]> buffer)
        {
            if (_peer == null)
            {
                throw new Exception("[UserToken/OnMessage] peer is null");
            }

            _peer.OnMessageFromClient(buffer);
        }

        public void Send(Packet msg)
        {
            Packet clone = new();
            msg.CopyTo(clone);

            lock (_lockSendingQueue)
            {
                bool is_sending = _sendingQueue.Count > 0;
                _sendingQueue.Enqueue(clone);

                if (!is_sending)
                {
                    StartSend();
                }
            }
        }

        public void StartSend()
        {
            lock (_lockSendingQueue)
            {
                if (IsReleased || Socket == null)
                {
                    return;
                }

                Packet packet = _sendingQueue.Peek();
                packet.RecordSize();

                SendEventArgs!.SetBuffer(SendEventArgs.Offset, packet.Position);
                Array.Copy(packet.Buffer, 0, SendEventArgs.Buffer!, SendEventArgs.Offset, packet.Position);

                if (!Socket.SendAsync(SendEventArgs))
                {
                    ProcessSend(SendEventArgs);
                }
            }
        }

        public void ProcessSend(SocketAsyncEventArgs sendArgs)
        {
            if (sendArgs.SocketError != SocketError.Success || sendArgs.BytesTransferred <= 0)
            {
                throw new Exception($"[ProcessSend] SocketError:{sendArgs.SocketError}, bytesTransferred:{sendArgs.BytesTransferred}");
            }

            lock (_lockSendingQueue)
            {
                // 보낼 것이 없음
                if (_sendingQueue.Count <= 0)
                {
                    return;
                }

                // 전송 완료
                if (_sendingQueue.Sum(buffer => buffer.Position) <= sendArgs.BytesTransferred)
                {
                    _sendingQueue.Clear();
                    return;
                }

                int sum = 0;
                while (true)
                {
                    sum += _sendingQueue.Peek().Position;

                    // 이미 보낸 패킷이므로 제거
                    if (sum <= sendArgs.BytesTransferred)
                    {
                        _sendingQueue.Dequeue();
                        continue;
                    }
                    break;
                }
                StartSend();
            }
        }

        public void Disconnect()
        {
            lock (_lockDisconnect)
            {
                if (IsReleased)
                {
                    return;
                }

                IsReleased = true;
                IsAlive = false;

                try
                {
                    if (Socket != null && Socket.Connected)
                    {
                        Socket.Shutdown(SocketShutdown.Both);
                        Socket.Close();
                    }
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }

                OnRemoved();
                Disconnected?.Invoke(this);
            }
        }

        public void OnRemoved()
        {
            lock (_lockSendingQueue)
            {
                IsReleased = true;
                _sendingQueue.Clear();
            }

            if (_heartbeatTimer != null)
            {
                using var waitHandle = new ManualResetEvent(false);
                _heartbeatTimer.Dispose(waitHandle);
                waitHandle.WaitOne();  // 타이머가 완전히 종료될 때까지 대기
                _heartbeatTimer = null;
            }

            _peer?.OnRemoved();
        }
    }
}
