using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;

namespace network.core;

public class UserToken
{
    private readonly object _lockSendingQueue = new();
    private readonly MessageResolver _messageResolver = new();
    private readonly Queue<Packet> _sendingQueue = new();
    public readonly SemaphoreSlim LockDisconnect = new(1);
    private Timer? _heartbeatTimer;
    private IPeer? _peer;
    public SocketAsyncEventArgs? ReceiveEventArgs { get; private set; }
    public SocketAsyncEventArgs? SendEventArgs { get; private set; }
    public Socket? Socket { get; set; }
    public bool IsReleased { get; set; }
    public event Action<UserToken>? Disconnected;

    public virtual void SetPeer(IPeer peer)
    {
        _peer = peer;
        IsReleased = false;
    }

    // ReSharper disable once UnusedMember.Global
    public void SetHeartbeatTimer()
    {
        _heartbeatTimer = new Timer(_ =>
            {
                var msg = Packet.Create((int)Protocol.C_TO_U_HEART_BEAT);
                Send(msg);
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3)
        );
    }

    public void SetEventArgs(SocketAsyncEventArgs receiveEventArgs, SocketAsyncEventArgs sendEventArgs)
    {
        ReceiveEventArgs = receiveEventArgs;
        SendEventArgs = sendEventArgs;
    }

    public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred)
    {
        return _messageResolver.OnReceived(buffer, offset, transferred, OnMessage);
    }

    private void OnMessage(Const<byte[]> buffer)
    {
        if (_peer == null)
        {
            throw new Exception("[UserToken/OnMessage] peer is null");
        }

        _peer.OnMessageFromClient(buffer);
    }

    public virtual void Send(Packet msg)
    {
        Packet clone = new();
        msg.CopyTo(clone);

        lock (_lockSendingQueue)
        {
            var isSending = _sendingQueue.Count > 0;
            _sendingQueue.Enqueue(clone);

            if (!isSending) StartSend();
        }
    }

    private void StartSend()
    {
        lock (_lockSendingQueue)
        {
            if (IsReleased || Socket == null) return;

            var packet = _sendingQueue.Peek();
            packet.RecordSize();

            SendEventArgs!.SetBuffer(SendEventArgs.Offset, packet.Position);
            Array.Copy(packet.Buffer, 0, SendEventArgs.Buffer!, SendEventArgs.Offset, packet.Position);

            if (!Socket.SendAsync(SendEventArgs)) ProcessSend(SendEventArgs);
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
            if (_sendingQueue.Count <= 0) return;

            // 전송 완료
            if (_sendingQueue.Sum(buffer => buffer.Position) <= sendArgs.BytesTransferred)
            {
                _sendingQueue.Clear();
                return;
            }

            var sum = 0;
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
        if (Socket is { Connected: true })
        {
            Socket.Shutdown(SocketShutdown.Both);
            Socket.Close();
        }

        OnRemoved();
        Disconnected?.Invoke(this);
    }

    public void OnRemoved()
    {
        lock (_lockSendingQueue)
        {
            _sendingQueue.Clear();
        }

        if (_heartbeatTimer != null)
        {
            using var waitHandle = new ManualResetEvent(false);
            _heartbeatTimer.Dispose(waitHandle);
            waitHandle.WaitOne(); // 타이머가 완전히 종료될 때까지 대기
            _heartbeatTimer = null;
        }

        _peer?.OnRemoved();
    }
}
