using System.Net.Sockets;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;

namespace network.core;

/// <summary>
///     Implements packet framing, bounded receive dispatch, queued sending, and graceful send-then-close behavior for
///     <see cref="UserToken"/>.
/// </summary>
public partial class UserToken
{
    public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred)
    {
        return _messageResolver.OnReceived(buffer, offset, transferred, OnMessage);
    }

    private void OnMessage(Const<byte[]> buffer)
    {
        if (!IsAcceptingMessages)
            return;

        RefreshAuthenticatedIdleDeadline();

        var peer = Volatile.Read(ref _peer);
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

    private void RefreshAuthenticatedIdleDeadline()
    {
        Timer? idleTimer;
        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                Volatile.Read(ref _authenticated) == 0 ||
                Volatile.Read(ref _closeAfterSend) != 0)
            {
                return;
            }

            Volatile.Write(
                ref _authenticatedIdleDeadlineMilliseconds,
                Environment.TickCount64 + GetAuthenticatedIdleTimeoutMilliseconds());
            idleTimer = Volatile.Read(ref _authenticatedIdleTimer);
        }

        if (idleTimer == null)
            return;

        try
        {
            idleTimer.Change(
                TimeSpan.FromSeconds(Config.AUTHENTICATED_IDLE_TIMEOUT_SECONDS),
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 정상적인 연결 종료와 packet dispatch가 경합한 경우다.
        }
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

    public bool TrySend(Packet msg)
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

        bool overflow;
        bool shouldStartSend = false;
        lock (_sendingQueueLock)
        {
            if (IsReleased || Socket == null || Volatile.Read(ref _closeAfterSend) != 0)
            {
                clone.Dispose();
                return false;
            }

            overflow = _sendingQueue.Count >= MaxQueuedSendPackets ||
                       _queuedSendBytes + clone.Position > MaxQueuedSendBytes;
            if (!overflow)
            {
                shouldStartSend = _sendingQueue.Count == 0;
                _sendingQueue.Enqueue(clone);
                _queuedSendBytes += clone.Position;
            }
        }

        if (!overflow)
        {
            if (shouldStartSend)
                StartSend();
            return !IsReleased;
        }

        clone.Dispose();
        RequestClose(ConnectionCloseReason.SendQueueOverflow);
        return false;
    }

    /// <summary>
    ///     응답 packet을 전송 queue에 넣는 것과 graceful close 전환을 하나의 상태 전이로 확정한다.
    ///     인증 timeout이 두 동작 사이를 선점해 마지막 응답을 버리는 경합을 막기 위한 API다.
    /// </summary>
    public bool TrySendAndDisconnect(Packet msg)
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

        bool overflow;
        bool shouldStartSend = false;
        lock (_stateTransitionLock)
        {
            if (Volatile.Read(ref _state) != StateActive ||
                IsReleased ||
                Socket == null ||
                Volatile.Read(ref _closeAfterSend) != 0)
            {
                clone.Dispose();
                return false;
            }

            lock (_sendingQueueLock)
            {
                overflow = _sendingQueue.Count >= MaxQueuedSendPackets ||
                           _queuedSendBytes + clone.Position > MaxQueuedSendBytes;
                if (!overflow)
                {
                    shouldStartSend = _sendingQueue.Count == 0;
                    _sendingQueue.Enqueue(clone);
                    _queuedSendBytes += clone.Position;
                    Volatile.Write(ref _closeAfterSend, 1);
                }
            }
        }

        if (overflow)
        {
            clone.Dispose();
            RequestClose(ConnectionCloseReason.SendQueueOverflow);
            return false;
        }

        if (shouldStartSend) StartSend();
        ArmGracefulCloseTimer();
        return true;
    }

    private void StartSend()
    {
        if (!TryBeginOperation()) return;

        bool completionOwnsOperation = false;
        try
        {
            Socket socket;
            SocketAsyncEventArgs sendEventArgs;
            lock (_sendingQueueLock)
            {
                if (IsReleased || _sendingQueue.Count == 0) return;

                socket = Socket ?? throw new InvalidOperationException("The send socket is not initialized.");
                sendEventArgs = SendEventArgs ??
                                throw new InvalidOperationException("The send event args are not initialized.");
                if (sendEventArgs.Buffer == null)
                    throw new InvalidOperationException("The send buffer is not initialized.");

                var packet = _sendingQueue.Peek();
                if (_sendOffset == 0) packet.RecordSize();

                int remaining = packet.Position - _sendOffset;
                if (remaining <= 0 || remaining > Config.BUFFER_SIZE)
                    throw new InvalidOperationException(
                        $"Invalid send offset {_sendOffset} for packet size {packet.Position}.");

                sendEventArgs.SetBuffer(sendEventArgs.Offset, remaining);
                Array.Copy(packet.Buffer, _sendOffset, sendEventArgs.Buffer, sendEventArgs.Offset, remaining);
            }

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
        Exception? sendFailure = null;
        bool shouldCloseAfterSend = false;
        bool shouldStartNextSend = false;
        try
        {
            if (IsReleased) return;

            if (sendArgs.LastOperation != SocketAsyncOperation.Send ||
                sendArgs.SocketError != SocketError.Success ||
                sendArgs.BytesTransferred <= 0)
            {
                sendFailure = sendArgs.SocketError == SocketError.Success
                    ? new InvalidOperationException(
                        $"Invalid send completion: {sendArgs.LastOperation}, {sendArgs.BytesTransferred} bytes.")
                    : new SocketException((int)sendArgs.SocketError);
            }
            else
            {
                lock (_sendingQueueLock)
                {
                    if (IsReleased) return;
                    if (_sendingQueue.Count == 0)
                    {
                        sendFailure = new InvalidOperationException("Send completed without a queued packet.");
                    }
                    else
                    {
                        var packet = _sendingQueue.Peek();
                        _sendOffset += sendArgs.BytesTransferred;
                        if (_sendOffset > packet.Position)
                        {
                            sendFailure = new InvalidOperationException(
                                $"Sent {_sendOffset} bytes for packet size {packet.Position}.");
                        }
                        else if (_sendOffset < packet.Position)
                        {
                            shouldStartNextSend = true;
                        }
                        else
                        {
                            var completedPacket = _sendingQueue.Dequeue();
                            _queuedSendBytes = Math.Max(0, _queuedSendBytes - completedPacket.Position);
                            completedPacket.Dispose();
                            _sendOffset = 0;
                            shouldStartNextSend = _sendingQueue.Count > 0;
                            shouldCloseAfterSend = !shouldStartNextSend &&
                                                   Volatile.Read(ref _closeAfterSend) != 0;
                        }
                    }
                }
            }

            if (sendFailure != null)
            {
                RequestClose(ConnectionCloseReason.SendError, sendFailure);
                return;
            }

            if (shouldCloseAfterSend)
            {
                Interlocked.Exchange(ref _gracefulCloseTimer, null)?.Dispose();
                RequestClose(ConnectionCloseReason.ExplicitDisconnect);
                return;
            }

            if (shouldStartNextSend) StartSend();
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
