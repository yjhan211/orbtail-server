using System.Net.Sockets;
using network.common;
using network.packets;

namespace network.core;

/// <summary>
///     TcpConnection의 패킷 수신과 송신을 처리한다.
///
///     수신한 TCP 데이터를 완성된 패킷으로 조립해 세션에 전달하며,
///     처리 대기 중인 메시지는 최대 128개로 제한한다.(MessageQueueOverflow)
///
///     송신할 패킷은 SendQueue에 저장하고 순서대로 전송한다.
///     마지막 응답을 보낸 뒤 연결을 종료해야 할 때는 패킷 추가와 종료 예약을
///     하나의 작업으로 처리한다.
/// </summary>
public partial class TcpConnection
{
    public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred)
    {
        return _messageResolver.OnReceived(buffer, offset, transferred, OnMessage);
    }

    private void OnMessage(ReadOnlyMemory<byte> buffer)
    {
        if (!IsAcceptingMessages)
            return;

        _timeouts.Touch();

        var session = Volatile.Read(ref _session);
        if (session == null)
            throw new InvalidOperationException("A message arrived before the connection session was initialized.");

        if (Interlocked.Increment(ref _pendingMessages) > MaxPendingMessages)
        {
            Interlocked.Decrement(ref _pendingMessages);
            RequestClose(ConnectionCloseReason.MessageQueueOverflow);
            return;
        }

        if (!TryBeginOperation())
        {
            Interlocked.Decrement(ref _pendingMessages);
            return;
        }

        _ = DispatchMessageAsync(session, buffer);
    }

    private async Task DispatchMessageAsync(IConnectionSession session, ReadOnlyMemory<byte> buffer)
    {
        try
        {
            await session.OnMessageFromClient(buffer);
        }
        catch (Exception ex)
        {
            RequestClose(ConnectionCloseReason.MalformedPacket, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingMessages);
            CompleteOperation();
        }
    }

    public virtual void Send(Packet msg)
    {
        TrySend(msg);
    }

    public bool TrySend(Packet msg)
    {
        var clone = ClonePacket(msg);
        var result = _sendQueue.TryEnqueue(
            clone,
            () => IsReleased || Socket == null || Volatile.Read(ref _closeAfterSend) != 0);
        return FinishEnqueue(clone, result) && !IsReleased;
    }

    public bool TrySendAndDisconnect(Packet msg)
    {
        var clone = ClonePacket(msg);
        SendQueue.EnqueueResult result;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Socket == null ||
                Volatile.Read(ref _closeAfterSend) != 0)
            {
                clone.Dispose();
                return false;
            }

            result = _sendQueue.TryEnqueue(clone, static () => false);
            if (result != SendQueue.EnqueueResult.Overflow)
                Volatile.Write(ref _closeAfterSend, 1);
        }

        if (!FinishEnqueue(clone, result))
            return false;

        _timeouts.ArmGracefulClose(() => RequestClose(ConnectionCloseReason.ExplicitDisconnect));
        return true;
    }

    private static Packet ClonePacket(Packet msg)
    {
        var clone = PacketBufferPool.Pop();
        try
        {
            msg.CopyTo(clone);
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private bool FinishEnqueue(Packet clone, SendQueue.EnqueueResult result)
    {
        switch (result)
        {
            case SendQueue.EnqueueResult.Refused:
                clone.Dispose();
                return false;
            case SendQueue.EnqueueResult.Overflow:
                clone.Dispose();
                RequestClose(ConnectionCloseReason.SendQueueOverflow);
                return false;
            case SendQueue.EnqueueResult.Started:
                StartSend();
                return true;
            default:
                return true;
        }
    }

    private void StartSend()
    {
        if (!TryBeginOperation()) return;

        bool completionOwnsOperation = false;
        try
        {
            if (IsReleased) return;

            var socket = Socket ?? throw new InvalidOperationException("The send socket is not initialized.");
            var sendEventArgs = SendEventArgs ??
                                throw new InvalidOperationException("The send event args are not initialized.");
            if (!_sendQueue.TryStageNext(sendEventArgs)) return;

            bool willRaiseEvent = socket.SendAsync(sendEventArgs);
            completionOwnsOperation = true;
            if (!willRaiseEvent) ProcessSend(sendEventArgs);
        }
        catch (Exception ex)
        {
            RequestClose(ConnectionCloseReason.SendError, ex);
        }
        finally
        {
            if (!completionOwnsOperation) CompleteOperation();
        }
    }

    internal void ProcessSend(SocketAsyncEventArgs sendArgs)
    {
        try
        {
            if (IsReleased) return;

            if (sendArgs.LastOperation != SocketAsyncOperation.Send ||
                sendArgs.SocketError != SocketError.Success ||
                sendArgs.BytesTransferred <= 0)
            {
                Exception failure = sendArgs.SocketError == SocketError.Success
                    ? new InvalidOperationException(
                        $"Invalid send completion: {sendArgs.LastOperation}, {sendArgs.BytesTransferred} bytes.")
                    : new SocketException((int)sendArgs.SocketError);
                RequestClose(ConnectionCloseReason.SendError, failure);
                return;
            }

            switch (_sendQueue.Advance(sendArgs.BytesTransferred))
            {
                case SendQueue.AdvanceResult.Invalid:
                    RequestClose(ConnectionCloseReason.SendError,
                        new InvalidOperationException("Send completion did not match the queued packet."));
                    return;
                case SendQueue.AdvanceResult.Continue:
                case SendQueue.AdvanceResult.NextPacket:
                    StartSend();
                    return;
                case SendQueue.AdvanceResult.Drained:
                    if (Volatile.Read(ref _closeAfterSend) != 0)
                        RequestClose(ConnectionCloseReason.ExplicitDisconnect);
                    return;
            }
        }
        catch (Exception ex)
        {
            RequestClose(ConnectionCloseReason.SendError, ex);
        }
        finally
        {
            CompleteOperation();
        }
    }
}
