using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;

namespace network.core;

/// <summary>
///     UserToken의 송수신 절반. 수신 바이트를 프레임으로 잘라 세션에 넘기고(동시 처리 상한 128), 송신은
///     <see cref="SendQueue" />에 넣어 한 번에 한 패킷씩 소켓에 건다. 마지막 응답을 보내고 끊는 경로는
///     "큐에 넣기"와 "끊기 예약"을 상태 잠금 안에서 한 번에 확정한다.
/// </summary>
public partial class UserToken
{
    public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred)
    {
        return _messageResolver.OnReceived(buffer, offset, transferred, OnMessage);
    }

    /// <summary>프레임 하나가 완성될 때마다 리졸버가 부른다. 세션 핸들러는 비동기로 넘기고 진행 중 작업으로 센다.</summary>
    private void OnMessage(Const<byte[]> buffer)
    {
        if (!IsAcceptingMessages)
            return;

        _timeouts.Touch();

        IPeer? peer = Volatile.Read(ref _peer);
        if (peer == null)
            throw new InvalidOperationException("A message arrived before the session peer was initialized.");

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

        _ = DispatchMessageAsync(peer, buffer);
    }

    private async Task DispatchMessageAsync(IPeer peer, Const<byte[]> buffer)
    {
        try
        {
            await peer.OnMessageFromClient(buffer);
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

    /// <summary>패킷을 복제해 큐에 넣는다. 닫히는 중이면 false, 큐가 넘치면 연결을 닫고 false.</summary>
    public bool TrySend(Packet msg)
    {
        Packet clone = ClonePacket(msg);
        SendQueue.EnqueueResult result = _sendQueue.TryEnqueue(
            clone,
            () => IsReleased || Socket == null || Volatile.Read(ref _closeAfterSend) != 0);
        return FinishEnqueue(clone, result) && !IsReleased;
    }

    /// <summary>
    ///     응답을 큐에 넣는 것과 "이 뒤로 끊는다"를 상태 잠금 안에서 한 번에 확정한다. 인증 타임아웃이 두 동작 사이에
    ///     끼어들어 마지막 응답을 버리는 경합을 막는다. 전송이 1초 안에 안 끝나도 끊는다.
    /// </summary>
    public bool TrySendAndDisconnect(Packet msg)
    {
        Packet clone = ClonePacket(msg);
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
        Packet clone = PacketBufferPool.Pop();
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

    /// <summary>큐 결과를 마무리한다. 거절이면 복제본을 버리고, 넘침이면 연결을 닫고, 큐가 비어 있었으면 송신을 시작한다.</summary>
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

    /// <summary>선두 패킷의 다음 조각을 소켓에 건다. 완료 콜백(<see cref="ProcessSend" />)이 진행 중 작업을 이어받는다.</summary>
    private void StartSend()
    {
        if (!TryBeginOperation()) return;

        bool completionOwnsOperation = false;
        try
        {
            if (IsReleased) return;

            Socket socket = Socket ?? throw new InvalidOperationException("The send socket is not initialized.");
            SocketAsyncEventArgs sendEventArgs = SendEventArgs ??
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

    /// <summary>송신 완료. 남은 조각·다음 패킷이 있으면 이어 보내고, 큐가 비었는데 끊기 예약이 있으면 끊는다.</summary>
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
                    if (Volatile.Read(ref _closeAfterSend) == 0) return;
                    _timeouts.DisarmGracefulClose();
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
