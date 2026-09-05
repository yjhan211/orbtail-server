using System.Buffers.Binary;
using network.common;

namespace network.packets;

/// <summary>
///     송신할 패킷을 만들거나 수신한 패킷의 내용을 읽는 클래스.
///
///     패킷 구조:
///         [길이 4바이트][프로토콜 번호 4바이트][플레이어 ID 8바이트][MessagePack 본문]
///     길이 필드에는 자기 자신 4바이트를 제외한 나머지 크기를 기록한다.
///
///     송신할 때는 프로토콜 번호와 플레이어 ID, 직렬화된 본문을 기록하고,
///     수신할 때는 같은 순서로 내용을 읽는다.
///     Position은 다음에 읽거나 쓸 위치를 나타낸다.
///
///     버퍼는 2KB로 시작하며 필요한 경우 전체 패킷 상한인 64KB까지 늘어난다.
///     Create로 풀에서 빌리고, 사용이 끝나면 Dispose로 반납한다.
///     반납할 때 큰 버퍼는 기본 크기로 줄이며, 반납한 객체는 다시 사용하면 안 된다.
/// </summary>
public class Packet : IDisposable
{
    private long _playerId;
    private int _readLimit = Config.BUFFER_SIZE;
    public int ProtocolId;

    internal Packet()
    {
        Buffer = new byte[Config.BUFFER_SIZE];
    }

    private void EnsureCapacity(int required)
    {
        if (required <= Buffer.Length) return;
        if (required > Config.MAX_MESSAGE_SIZE)
            throw new InvalidOperationException(
                $"Packet size {required} exceeds MAX_MESSAGE_SIZE {Config.MAX_MESSAGE_SIZE}.");

        Array.Resize(ref _buffer, required);
    }

    private byte[] _buffer = [];

    public byte[] Buffer
    {
        get => _buffer;
        private set => _buffer = value;
    }

    public int Position { get; private set; }

    public byte[] ToBytes()
    {
        byte[] data = new byte[Position];
        Array.Copy(Buffer, data, Position);
        return data;
    }

    public void Dispose()
    {
        Destroy(this);
        GC.SuppressFinalize(this);
    }

    public static Packet Create(int protocolId, long playerId = 0)
    {
        var packet = PacketBufferPool.Pop();
        packet.SetProtocolId(protocolId);
        packet.SetPlayerId(playerId);

        return packet;
    }

    public static Packet Create(ReadOnlyMemory<byte> buffer)
    {
        if (Config.MAX_MESSAGE_SIZE < buffer.Length)
            throw new Exception($"Invalid Buffer Size. size:{buffer.Length}");

        var packet = PacketBufferPool.Pop();
        packet.LoadForReading(buffer.Span);
        return packet;
    }

    public static Packet CreateForSending(byte[] wireBytes)
    {
        ArgumentNullException.ThrowIfNull(wireBytes);

        const int routingBodySize = sizeof(int) + sizeof(long);
        int minimumPacketSize = Config.HEADER_SIZE + routingBodySize;
        if (wireBytes.Length < minimumPacketSize || wireBytes.Length > Config.MAX_MESSAGE_SIZE)
        {
            throw new ArgumentException(
                $"Wire packet length must be between {minimumPacketSize} and {Config.MAX_MESSAGE_SIZE} bytes.",
                nameof(wireBytes));
        }

        int recordedBodySize = BinaryPrimitives.ReadInt32LittleEndian(
            wireBytes.AsSpan(0, Config.HEADER_SIZE));
        if (recordedBodySize != wireBytes.Length - Config.HEADER_SIZE ||
            recordedBodySize < routingBodySize)
        {
            throw new ArgumentException("Wire packet header does not match its body length.", nameof(wireBytes));
        }

        int protocolId = BinaryPrimitives.ReadInt32LittleEndian(
            wireBytes.AsSpan(Config.HEADER_SIZE, sizeof(int)));
        if (!Enum.IsDefined(typeof(Protocol), protocolId))
            throw new ArgumentException("Wire packet contains an unknown protocol id.", nameof(wireBytes));

        long playerId = BinaryPrimitives.ReadInt64LittleEndian(
            wireBytes.AsSpan(Config.HEADER_SIZE + sizeof(int), sizeof(long)));
        var packet = PacketBufferPool.Pop();
        try
        {
            packet.ProtocolId = protocolId;
            packet._playerId = playerId;
            packet.Overwrite(wireBytes, wireBytes.Length);
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private static void Destroy(Packet packet)
    {
        packet.Position = 0;
        if (packet.Buffer.Length > Config.BUFFER_SIZE)
            packet.Buffer = new byte[Config.BUFFER_SIZE];
        packet._readLimit = packet.Buffer.Length;
        PacketBufferPool.Push(packet);
    }

    public void CopyTo(Packet target)
    {
        target.ProtocolId = ProtocolId;
        target._playerId = _playerId;
        target.Overwrite(Buffer, Position);
    }

    private void Overwrite(byte[] source, int position)
    {
        if (position < 0 || position > source.Length)
            throw new ArgumentOutOfRangeException(nameof(position));

        EnsureCapacity(position);
        Array.Copy(source, 0, Buffer, 0, position);
        Position = position;
        _readLimit = position;
    }

    private void LoadForReading(ReadOnlySpan<byte> source)
    {
        EnsureCapacity(source.Length);
        source.CopyTo(Buffer);
        Position = Config.HEADER_SIZE;
        _readLimit = source.Length;
    }

    private void SetProtocolId(int protocolId)
    {
        ProtocolId = protocolId;
        Position = Config.HEADER_SIZE;
        _readLimit = Buffer.Length;
        byte[] tempBuffer = BitConverter.GetBytes(ProtocolId);
        tempBuffer.CopyTo(Buffer, Position);
        Position += tempBuffer.Length;
    }

    public int PopProtocolId()
    {
        int data = BitConverter.ToInt32(Buffer, Position);
        Position += sizeof(int);

        return data;
    }

    private void SetPlayerId(long playerId)
    {
        _playerId = playerId;
        byte[] tempBuffer = BitConverter.GetBytes(playerId);
        tempBuffer.CopyTo(Buffer, Position);
        Position += tempBuffer.Length;
    }

    public long PopPlayerId()
    {
        long data = BitConverter.ToInt64(Buffer, Position);
        Position += sizeof(long);

        return data;
    }

    public void SetBody(byte[] serializedBuffer)
    {
        EnsureCapacity(Position + serializedBuffer.Length);
        serializedBuffer.CopyTo(Buffer, Position);
        Position += serializedBuffer.Length;
    }

    public byte[] PopBody()
    {
        byte[] tempBuffer = new byte[_readLimit - Position];
        Array.Copy(Buffer, Position, tempBuffer, 0, tempBuffer.Length);
        Position += tempBuffer.Length;

        return tempBuffer;
    }

    public void RecordSize()
    {
        int bodySize = Position - Config.HEADER_SIZE;
        byte[] header = BitConverter.GetBytes(bodySize);
        header.CopyTo(Buffer, 0);
    }
}
