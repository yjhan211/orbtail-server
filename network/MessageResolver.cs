namespace network
{
    class MessageResolver
    {
        public delegate void CompleteMessageCallback(Const<byte[]> buffer);
        readonly byte[] message_buffer = new byte[Config.BUFFER_SIZE];
        int start_position = 0;
        int current_position = 0;
        int target_position = 0;
        int remain_bytes = 0;

        public (ErrorCode error_code, string? error_log) OnReceived(
            byte[] buffer,
            int offset,
            int transferred,
            CompleteMessageCallback callback
        )
        {
            this.start_position = offset;
            this.remain_bytes = transferred;

            try
            {
                while (this.remain_bytes > 0)
                {
                    // 헤더 복사
                    if (this.current_position < Config.HEADER_SIZE)
                    {
                        this.target_position = Config.HEADER_SIZE;
                        if (!CopyBuffer(buffer, ref start_position))
                        {
                            // 헤더가 덜 왔음. 다음 수신 기다림
                            return (ErrorCode.SUCCESS, null);
                        }

                        int message_size = this.ParseHeader();
                        if (message_size > Config.BUFFER_SIZE)
                        {
                            return (ErrorCode.FATAL, "Message size exceeds maximum allowed size");
                        }

                        this.target_position += message_size;
                    }

                    if (this.target_position > Config.BUFFER_SIZE)
                    {
                        return (ErrorCode.FATAL, "Target position exceeds buffer size");
                    }

                    // 메세지 복사
                    if (!CopyBuffer(buffer, ref start_position))
                    {
                        // 메세지가 덜 왔음. 다음 수신 기다림
                        return (ErrorCode.SUCCESS, null);
                    }

                    // 메세지 처리
                    callback(new Const<byte[]>(this.message_buffer));

                    // 메세지 초기화
                    ClearBuffer();
                }
                return (ErrorCode.SUCCESS, null);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
                return (ErrorCode.FATAL, null);
            }
        }

        bool CopyBuffer(byte[] buffer, ref int start_position)
        {
            int copy_size = this.target_position - this.current_position;
            if (this.remain_bytes < copy_size)
            {
                copy_size = this.remain_bytes;
            }

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
