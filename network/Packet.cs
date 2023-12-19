#pragma warning disable CS8618

namespace network
{
    public class Packet
    {
        public IPeer owner { get; private set; }
        public byte[] buffer { get; private set; }
        public int position { get; private set; }
        public int protocol_id { get; private set; }

        public static Packet Create(int protocol_id)
        {
            Packet packet = PacketBufferManager.Pop();
            packet.SetProtocolId(protocol_id);
            return packet;
        }

        public static void Destroy(Packet packet)
        {
            packet.position = 0;
            PacketBufferManager.Push(packet);
        }

        public Packet(byte[] buffer, IPeer owner)
        {
            this.buffer = buffer;
            this.owner = owner;
            this.position = Config.HEADER_SIZE;
        }

        public Packet()
        {
            this.buffer = new byte[Config.BUFFER_SIZE];
        }

        public void CopyTo(Packet target)
        {
            target.SetProtocolId(this.protocol_id);
            target.Overwrite(this.buffer, this.position);
        }

        public void Overwrite(byte[] source, int position)
        {
            Array.Copy(source, this.buffer, source.Length);
            this.position = position;
        }

        public void SetProtocolId(Int32 protocol_id)
        {
            this.protocol_id = protocol_id;
            this.position = Config.HEADER_SIZE;

            byte[] temp_buffer = BitConverter.GetBytes(protocol_id);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public Int32 PopProtocolId()
        {
            Int32 data = BitConverter.ToInt32(this.buffer, this.position);
            this.position += sizeof(Int32);

            return data;
        }

        public void SetBody(byte[] serizlized_buffer)
        {
            if (Config.BUFFER_SIZE < serizlized_buffer.Length)
            {
                throw new Exception($"BUFFER SIZE OVER. {serizlized_buffer.Length}");
            }

            serizlized_buffer.CopyTo(this.buffer, this.position);
            this.position += serizlized_buffer.Length;
        }

        public byte[] PopBody()
        {
            byte[] temp_buffer = new byte[this.buffer.Length - this.position];
            Array.Copy(this.buffer, this.position, temp_buffer, 0, temp_buffer.Length);
            this.position += temp_buffer.Length;

            return temp_buffer;
        }

        public void RecordSize()
        {
            Int32 body_size = (Int32)(this.position - Config.HEADER_SIZE);
            byte[] header = BitConverter.GetBytes(body_size);
            header.CopyTo(this.buffer, 0);
        }
    }
}
