using System.Net.Sockets;
using network.common;
using network.packets;

namespace network.core;

/// <summary>
///     연결 하나의 송신 대기열. 패킷 수·바이트 상한을 넘기면 넣지 않고 Overflow를 돌려주며, 연결을 닫는 건 호출자의 몫이다.
///     소켓에는 한 번에 한 패킷만 걸고, 부분 전송은 오프셋으로 이어 보낸다.
///     잠금은 안에 있고 UserToken의 상태 잠금보다 안쪽이다 — 바깥에서 상태 잠금을 잡은 채 불러도 된다.
/// </summary>
internal sealed class SendQueue
{
    public const int MaxQueuedPackets = 128;
    public const int MaxQueuedBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly Queue<Packet> _packets = new();
    private int _queuedBytes;
    private int _sendOffset;

    public enum EnqueueResult
    {
        /// <summary>비어 있던 큐에 들어갔다 — 호출자가 송신을 시작해야 한다.</summary>
        Started,
        Queued,
        Overflow,
        /// <summary>호출자의 거절 조건이 잠금 안에서 참이었다 (닫히는 중 등).</summary>
        Refused
    }

    public enum AdvanceResult
    {
        Invalid,
        /// <summary>선두 패킷이 아직 남았다 — 이어서 보낸다.</summary>
        Continue,
        /// <summary>선두 패킷이 끝났고 다음 패킷이 있다.</summary>
        NextPacket,
        /// <summary>큐가 비었다.</summary>
        Drained
    }

    public int Count
    {
        get
        {
            lock (_gate) return _packets.Count;
        }
    }

    /// <summary>
    ///     <paramref name="refuse" />는 잠금 안에서 평가한다 — "닫히는 중인지 확인"과 "넣기"가 한 번에 일어나야
    ///     닫기의 <see cref="Clear" />와 어긋나 패킷이 큐에 남는 일이 없다.
    /// </summary>
    public EnqueueResult TryEnqueue(Packet packet, Func<bool> refuse)
    {
        lock (_gate)
        {
            if (refuse())
                return EnqueueResult.Refused;
            if (_packets.Count >= MaxQueuedPackets || _queuedBytes + packet.Position > MaxQueuedBytes)
                return EnqueueResult.Overflow;

            bool wasEmpty = _packets.Count == 0;
            _packets.Enqueue(packet);
            _queuedBytes += packet.Position;
            return wasEmpty ? EnqueueResult.Started : EnqueueResult.Queued;
        }
    }

    /// <summary>선두 패킷의 남은 바이트를 송신 버퍼에 복사한다. 큐가 비었으면 false. 길이 헤더는 첫 조각을 보낼 때 기록한다.</summary>
    public bool TryStageNext(SocketAsyncEventArgs sendArgs)
    {
        lock (_gate)
        {
            if (_packets.Count == 0)
                return false;
            if (sendArgs.Buffer == null)
                throw new InvalidOperationException("The send buffer is not initialized.");

            Packet packet = _packets.Peek();
            if (_sendOffset == 0)
                packet.RecordSize();

            int remaining = packet.Position - _sendOffset;
            if (remaining <= 0 || remaining > Config.BUFFER_SIZE)
                throw new InvalidOperationException(
                    $"Invalid send offset {_sendOffset} for packet size {packet.Position}.");

            sendArgs.SetBuffer(sendArgs.Offset, remaining);
            Array.Copy(packet.Buffer, _sendOffset, sendArgs.Buffer, sendArgs.Offset, remaining);
            return true;
        }
    }

    /// <summary>전송 완료 바이트를 반영한다. 패킷이 끝나면 버퍼를 풀에 돌려준다.</summary>
    public AdvanceResult Advance(int bytesTransferred)
    {
        lock (_gate)
        {
            if (_packets.Count == 0)
                return AdvanceResult.Invalid;

            Packet packet = _packets.Peek();
            _sendOffset += bytesTransferred;
            if (_sendOffset > packet.Position)
                return AdvanceResult.Invalid;
            if (_sendOffset < packet.Position)
                return AdvanceResult.Continue;

            _packets.Dequeue();
            _queuedBytes = Math.Max(0, _queuedBytes - packet.Position);
            packet.Dispose();
            _sendOffset = 0;
            return _packets.Count > 0 ? AdvanceResult.NextPacket : AdvanceResult.Drained;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_packets.Count > 0)
                _packets.Dequeue().Dispose();
            _queuedBytes = 0;
            _sendOffset = 0;
        }
    }
}
