using network.common;
using network.interfaces;
using network.utils;

namespace network.packets
{
    public class Packet : IPacket
    {
        private int _protocolId;
        private long _playerId;
        public byte[] Buffer { get; private set; }
        public int Position { get; private set; }

        public static Packet Create(int protocolId, long playerId = 0)
        {
            Packet packet = PacketBufferPool.Pop();
            packet.Buffer = new byte[Config.BUFFER_SIZE];
            packet.SetProtocolId(protocolId);
            packet.SetPlayerId(playerId);

            return packet;
        }

        public static Packet Create(Const<byte[]> buffer)
        {
            if (Config.BUFFER_SIZE < buffer.Value.Length)
            {
                throw new Exception($"Invalid Buffer Size. size:{buffer.Value.Length}");
            }

            byte[] clone = new byte[Config.BUFFER_SIZE];
            Array.Copy(buffer.Value, clone, buffer.Value.Length);
            return new(clone);
        }

        public static void Destroy(Packet packet)
        {
            packet.Position = 0;
            PacketBufferPool.Push(packet);
        }

        public Packet(byte[] buffer)
        {
            Buffer = buffer;
            Position = Config.HEADER_SIZE;
        }

        public Packet()
        {
            Buffer = new byte[Config.BUFFER_SIZE];
        }

        public void CopyTo(Packet target)
        {
            target.SetProtocolId(_protocolId);
            target.SetPlayerId(_playerId);
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

        public void Overwrite(byte[] source, int position)
        {
            Array.Copy(source, Buffer, source.Length);
            Position = position;
        }

        private void SetProtocolId(int protocolId)
        {
            _protocolId = protocolId;
            Position = Config.HEADER_SIZE;
            byte[] tempBuffer = BitConverter.GetBytes(_protocolId);
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

        public void SetBody(byte[] serizlizedBuffer)
        {
            if (Config.BUFFER_SIZE < serizlizedBuffer.Length)
            {
                throw new Exception($"BUFFER SIZE OVER. {serizlizedBuffer.Length}");
            }

            serizlizedBuffer.CopyTo(Buffer, Position);
            Position += serizlizedBuffer.Length;
        }

        public byte[] PopBody()
        {
            byte[] tempBuffer = new byte[Buffer.Length - Position];
            Array.Copy(Buffer, Position, tempBuffer, 0, tempBuffer.Length);
            Position += tempBuffer.Length;

            return tempBuffer;
        }

        public void RecordSize()
        {
            int bodySize = (Int32)(Position - Config.HEADER_SIZE);
            byte[] header = BitConverter.GetBytes(bodySize);
            header.CopyTo(Buffer, 0);
        }

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
    }
}
