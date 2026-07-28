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
    private int _sendOffset;
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
                using var msg = Packet.Create((int)Protocol.C_TO_U_HEART_BEAT);
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
        var clone = PacketBufferPool.Pop();
        try
        {
            msg.CopyTo(clone);
        }
        catch
        {
            clone.Dispose();
            throw;
        }

        lock (_lockSendingQueue)
        {
            if (IsReleased || Socket == null)
            {
                clone.Dispose();
                return;
            }

            bool shouldStartSend = _sendingQueue.Count == 0;
            _sendingQueue.Enqueue(clone);

            if (shouldStartSend) StartSendLocked();
        }
    }

    private void StartSendLocked()
    {
        if (IsReleased || Socket == null || _sendingQueue.Count == 0) return;

        var packet = _sendingQueue.Peek();
        if (_sendOffset == 0) packet.RecordSize();

        int remaining = packet.Position - _sendOffset;
        if (remaining <= 0)
            throw new InvalidOperationException($"Invalid send offset {_sendOffset} for packet size {packet.Position}");

        SendEventArgs!.SetBuffer(SendEventArgs.Offset, remaining);
        Array.Copy(packet.Buffer, _sendOffset, SendEventArgs.Buffer!, SendEventArgs.Offset, remaining);

        if (!Socket.SendAsync(SendEventArgs)) ProcessSend(SendEventArgs);
    }

    public void ProcessSend(SocketAsyncEventArgs sendArgs)
    {
        if (sendArgs.SocketError != SocketError.Success || sendArgs.BytesTransferred <= 0)
        {
            OnRemoved();
            throw new Exception($"[ProcessSend] SocketError:{sendArgs.SocketError}, bytesTransferred:{sendArgs.BytesTransferred}");
        }

        lock (_lockSendingQueue)
        {
            if (_sendingQueue.Count == 0)
            {
                _sendOffset = 0;
                return;
            }

            var packet = _sendingQueue.Peek();
            _sendOffset += sendArgs.BytesTransferred;
            if (_sendOffset < packet.Position)
            {
                StartSendLocked();
                return;
            }

            if (_sendOffset > packet.Position)
                throw new InvalidOperationException($"Sent {_sendOffset} bytes for packet size {packet.Position}");

            _sendingQueue.Dequeue().Dispose();
            _sendOffset = 0;
            StartSendLocked();
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
            if (IsReleased) return;
            IsReleased = true;
            while (_sendingQueue.Count > 0)
                _sendingQueue.Dequeue().Dispose();
            _sendOffset = 0;
        }

        if (_heartbeatTimer != null)
        {
            using var waitHandle = new ManualResetEvent(false);
            _heartbeatTimer.Dispose(waitHandle);
            waitHandle.WaitOne();
            _heartbeatTimer = null;
        }

        _peer?.OnRemoved();
    }
}
