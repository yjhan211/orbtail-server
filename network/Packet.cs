#pragma warning disable CS8618

using System.Text;

namespace network
{
    public class Packet
    {
        public IPeer owner { get; private set; }
        public byte[] buffer { get; private set; }
        public int position { get; private set; }
        public Int32 protocol_id { get; private set; }

        public static Packet Create(Int32 protocol_id)
        {
            Packet packet = PacketBufferManager.Pop();
            packet.SetProtocol(protocol_id);
            return packet;
        }

        public static void Destroy(Packet packet)
        {
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
            this.buffer = new byte[1024];
        }

        public Int32 PopProtocolId()
        {
            return PopInt32();
        }

        public void CopyTo(Packet target)
        {
            target.SetProtocol(this.protocol_id);
            target.Overwrite(this.buffer, this.position);
        }

        public void Overwrite(byte[] source, int position)
        {
            Array.Copy(source, this.buffer, source.Length);
            this.position = position;
        }

        public byte PopByte()
        {
            byte data = (byte)BitConverter.ToInt16(this.buffer, this.position);
            this.position = sizeof(byte);

            return data;
        }

        public Int16 PopInt16()
        {
            Int16 data = BitConverter.ToInt16(this.buffer, this.position);
            this.position = sizeof(Int16);
            return data;
        }

        public Int32 PopInt32()
        {
            Int32 data = BitConverter.ToInt32(this.buffer, this.position);
            this.position += sizeof(Int32);
            return data;
        }

        public string PopString()
        {
            // 문자열 길이는 최대 2바이트 까지. 0 ~ 32767
            Int16 len = BitConverter.ToInt16(this.buffer, this.position);
            this.position += sizeof(Int16);

            // 인코딩은 utf8로 통일한다.
            string data = System.Text.Encoding.UTF8.GetString(this.buffer, this.position, len);
            this.position += len;

            return data;
        }

        public void SetProtocol(Int32 protocol_id)
        {
            this.protocol_id = protocol_id;
            this.position = Config.HEADER_SIZE;

            Push(protocol_id);
        }

        public void RecordSize()
        {
            Int32 body_size = (Int32)(this.position - Config.HEADER_SIZE);
            byte[] header = BitConverter.GetBytes(body_size);
            header.CopyTo(this.buffer, 0);
        }

        public void PushInt16(Int16 data)
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void Push(byte data)
        {
            this.buffer[this.position] = data;
            this.position += sizeof(byte);
        }

        public void Push(Int16 data)
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void Push(Int32 data)
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void Push(string data)
        {
            byte[] temp_buffer = Encoding.UTF8.GetBytes(data);

            Int16 len = (Int16)temp_buffer.Length;
            byte[] len_buffer = BitConverter.GetBytes(len);
            len_buffer.CopyTo(this.buffer, this.position);
            this.position += sizeof(Int16);

            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }
    }
}
