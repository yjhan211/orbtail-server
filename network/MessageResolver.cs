namespace network
{
    class MessageResolver
    {
        public delegate void CompleteMessageCallback(Const<byte[]> buffer);
        readonly byte[] message_buffer = new byte[1024];

        int current_position = 0;
        int target_position = 0;
        int remain_bytes = 0;

        public void OnReceived(
            byte[] buffer,
            int offset,
            int transferred,
            CompleteMessageCallback callback
        )
        {
            int start_position = offset;
            this.remain_bytes = transferred;

            while (this.remain_bytes > 0)
            {
                // 헤더 복사
                if (this.current_position < Config.HEADER_SIZE)
                {
                    this.target_position = Config.HEADER_SIZE;
                    if (!CopyPacket(buffer, ref start_position))
                    {
                        // 헤더가 덜 왔음. 다음 수신 기다림
                        return;
                    }

                    this.target_position += this.ParseHeader();
                }

                // 메세지 복사
                if (!CopyPacket(buffer, ref start_position))
                {
                    // 메세지가 덜 왔음. 다음 수신 기다림
                    return;
                }

                // 메세지 처리
                callback(new Const<byte[]>(this.message_buffer));
                ClearBuffer();
            }
        }

        bool CopyPacket(byte[] buffer, ref int start_position)
        {
            try
            {
                int copy_size = this.target_position - this.current_position;
                if (this.remain_bytes < copy_size)
                {
                    copy_size = this.remain_bytes;
                }

                // Console.WriteLine(
                //     $"start_position: {start_position}, current_position:{this.current_position}, copy_size:{copy_size}"
                // );

                Array.Copy(
                    buffer,
                    start_position,
                    this.message_buffer,
                    this.current_position,
                    copy_size
                );

                start_position += copy_size;
                this.current_position += copy_size;
                this.remain_bytes -= copy_size;

                return this.current_position >= this.target_position;
            }
            catch (Exception e)
            {
                throw new Exception(
                    $"{e.Message}, {e.StackTrace} | start_position:{start_position}, length:{this.message_buffer.Length}, cur_pos:{this.current_position}, tar_pos:{this.target_position}"
                );
            }
        }

        int ParseHeader()
        {
            return BitConverter.ToInt32(this.message_buffer, 0);
        }

        void ClearBuffer()
        {
            Array.Clear(this.message_buffer, 0, this.message_buffer.Length);

            this.current_position = 0;
            this.target_position = 0;
        }
    }
}
