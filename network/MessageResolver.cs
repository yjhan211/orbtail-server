namespace network
{
    class MessageResolver
    {
        public delegate void CompleteMessageCallback(Const<byte[]> buffer);
        readonly byte[] message_buffer = new byte[1024];

        int current_position = 0;
        int target_position = 0;
        int remain_bytes = 0;

        public void onReceived(byte[] buffer, int offset, int transferred, CompleteMessageCallback callback)
        {
            int start_position = offset;
            this.remain_bytes = transferred;

            while (this.remain_bytes > 0)
            {
                // 헤더 복사
                if (this.current_position < Config.HEADER_SIZE)
                {
                    this.target_position = Config.HEADER_SIZE;
                    if (!copyPacket(buffer, ref start_position, offset, transferred))
                    {
                        // 헤더가 덜 왔음. 다음 수신 기다림
                        return;
                    }

                    this.target_position += this.parseHeader();
                }

                // 메세지 복사
                if (!copyPacket(buffer, ref start_position, offset, transferred))
                {
                    // 메세지가 덜 왔음. 다음 수신 기다림
                    return;
                }

                // 메세지 처리
                callback(new Const<byte[]>(this.message_buffer));
                clearBuffer();
            }
        }

        bool copyPacket(byte[] buffer, ref int start_position, int offset, int transferred)
        {
            // 더 이상 카피할 데이터 없음
            if (this.current_position >= offset + transferred)
            {
                return false;
            }

            int copy_size = this.target_position - this.current_position;
            if (this.remain_bytes < copy_size)
            {
                copy_size = this.remain_bytes;
            }

            Array.Copy(buffer, start_position, this.message_buffer, this.current_position, copy_size);

            start_position += copy_size;
            this.current_position += copy_size;
            this.remain_bytes -= copy_size;

            return this.current_position >= this.target_position;
        }

        int parseHeader()
        {
            return BitConverter.ToInt32(this.message_buffer, 0);
        }

        void clearBuffer()
        {
            Array.Clear(this.message_buffer, 0, this.message_buffer.Length);

            this.current_position = 0;
        }
    }
}
