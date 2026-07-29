using network.common;
using network.interfaces;
using network.utils;

namespace network.packets;

public class Packet : IPacket
{
    private long _playerId;
    private int _readLimit = Config.BUFFER_SIZE;
    public int ProtocolId;

    internal Packet()
    {
        Buffer = new byte[Config.BUFFER_SIZE];
    }

    public byte[] Buffer { get; private set; }
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
        if (Config.BUFFER_SIZE < buffer.Value.Length)
            throw new Exception($"Invalid Buffer Size. size:{buffer.Value.Length}");

        var packet = PacketBufferPool.Pop();
        packet.LoadForReading(buffer.Value);
        return packet;
    }

    private static void Destroy(Packet packet)
    {
        packet.Position = 0;
        packet._readLimit = packet.Buffer.Length;
        PacketBufferPool.Push(packet);
    }

    public void CopyTo(Packet target)
    {
        target.ProtocolId = ProtocolId;
        target._playerId = _playerId;
        target.Overwrite(Buffer, Position);
    }

    public void CopyTo(IPacket target)
    {
        if (target is Packet packet)
        {
            CopyTo(packet);
            return;
        }

        throw new NotImplementedException();
    }

    private void Overwrite(byte[] source, int position)
    {
        if (position < 0 || position > source.Length || position > Buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(position));

        Array.Copy(source, 0, Buffer, 0, position);
        Position = position;
        _readLimit = position;
    }

    private void LoadForReading(byte[] source)
    {
        if (source.Length > Buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(source));

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
        if (serializedBuffer.Length > Buffer.Length - Position)
            throw new Exception($"BUFFER SIZE OVER. {serializedBuffer.Length}");

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
