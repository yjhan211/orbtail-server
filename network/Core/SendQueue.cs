using System.Net.Sockets;
using network.common;
using network.packets;

namespace network.core;

/// <summary>
///     TCP 연결 하나에서 보낼 패킷을 순서대로 보관한다.
///
///     대기 중인 패킷 수나 전체 크기가 제한을 넘으면 패킷을 추가하지 않고
///     Overflow를 반환한다. 이때 연결을 종료할지는 TcpConnection이 결정한다.
///
///     패킷은 한 번에 하나씩, 송신 I/O 버퍼(BUFFER_SIZE) 크기로 잘라 전송하며,
///     일부만 전송된 경우에는 남은 위치부터 이어서 전송한다. 패킷 자체는 MAX_MESSAGE_SIZE까지 허용한다.
///
///     송신 큐의 데이터는 내부 잠금으로 보호한다.
///     TcpConnection 상태 잠금과 함께 사용할 때는 TcpConnection 잠금을 먼저 잡는다.
/// </summary>
internal sealed class SendQueue
{
    public const int MaxQueuedPackets = 128;
    private const int MaxQueuedBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly Queue<Packet> _packets = new();
    private int _queuedBytes;
    private int _sendOffset;

    public enum EnqueueResult
    {
        Started,
        Queued,
        Overflow,
        Refused
    }

    public enum AdvanceResult
    {
        Invalid,
        Continue,
        NextPacket,
        Drained
    }

    public int Count
    {
        get
        {
            lock (_gate) return _packets.Count;
        }
    }

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

    public bool TryStageNext(SocketAsyncEventArgs sendArgs)
    {
        lock (_gate)
        {
            if (_packets.Count == 0)
                return false;
            if (sendArgs.Buffer == null)
                throw new InvalidOperationException("The send buffer is not initialized.");

            var packet = _packets.Peek();
            if (_sendOffset == 0)
                packet.RecordSize();

            int remaining = packet.Position - _sendOffset;
            if (remaining <= 0)
                throw new InvalidOperationException(
                    $"Invalid send offset {_sendOffset} for packet size {packet.Position}.");

            int staged = Math.Min(remaining, Config.BUFFER_SIZE);
            sendArgs.SetBuffer(sendArgs.Offset, staged);
            Array.Copy(packet.Buffer, _sendOffset, sendArgs.Buffer, sendArgs.Offset, staged);
            return true;
        }
    }

    public AdvanceResult Advance(int bytesTransferred)
    {
        lock (_gate)
        {
            if (_packets.Count == 0)
                return AdvanceResult.Invalid;

            var packet = _packets.Peek();
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
