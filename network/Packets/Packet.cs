using System.Buffers.Binary;
using network.common;
using network.utils;

namespace network.packets;

/// <summary>
///     패킷 하나의 바이트 버퍼. 풀에서 빌려 쓰고 Dispose로 돌려준다.
///     버퍼는 I/O 버퍼 크기(BUFFER_SIZE)로 시작해 본문이 크면 MAX_MESSAGE_SIZE까지 자란다.
///     풀로 돌아갈 때는 기본 크기로 줄여 큰 패킷 하나가 풀 전체를 키우지 않게 한다.
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

    public static Packet Create(Const<byte[]> buffer)
    {
        if (Config.MAX_MESSAGE_SIZE < buffer.Value.Length)
            throw new Exception($"Invalid Buffer Size. size:{buffer.Value.Length}");

        var packet = PacketBufferPool.Pop();
        packet.LoadForReading(buffer.Value);
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

    private void LoadForReading(byte[] source)
    {
        EnsureCapacity(source.Length);
        Array.Copy(source, 0, Buffer, 0, source.Length);
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
