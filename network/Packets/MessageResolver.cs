using network.common;
using network.utils;

namespace network.packets
{
    class MessageResolver
    {
        private readonly byte[] _messageBuffer = new byte[Config.BUFFER_SIZE];
        private int _startPosition = 0;
        private int _currentPosition = 0;
        private int _targetPosition = 0;
        private int _remainBytes = 0;
        public delegate void CompleteMessageCallback(Const<byte[]> buffer);

        public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred, CompleteMessageCallback callback)
        {
            _startPosition = offset;
            _remainBytes = transferred;

            try
            {
                while (_remainBytes > 0)
                {
                    // 헤더 복사
                    if (_currentPosition < Config.HEADER_SIZE)
                    {
                        _targetPosition = Config.HEADER_SIZE;
                        if (!CopyBuffer(buffer, ref _startPosition))
                        {
                            // 헤더가 덜 왔음. 다음 수신 기다림
                            return (ErrorCode.SUCCESS, null);
                        }

                        int messageSize = ParseHeader();
                        if (messageSize <= 0 || messageSize > Config.BUFFER_SIZE - Config.HEADER_SIZE)
                        {
                            return (ErrorCode.FATAL, $"[MessageResolver/OnReceived] Invalid message size {messageSize}");
                        }

                        _targetPosition += messageSize;
                    }

                    if (_targetPosition > Config.BUFFER_SIZE)
                    {
                        return (ErrorCode.FATAL, "[MessageResolver/OnReceived] Target position exceeds buffer size");
                    }

                    // 메세지 복사
                    if (!CopyBuffer(buffer, ref _startPosition))
                    {
                        // 메세지가 덜 왔음. 다음 수신 기다림
                        return (ErrorCode.SUCCESS, null);
                    }

                    // 메세지 처리
                    callback(new Const<byte[]>(_messageBuffer));

                    // 메세지 초기화
                    ClearBuffer();
                }
                return (ErrorCode.SUCCESS, null);
            }
            catch (Exception e)
            {
                return (ErrorCode.FATAL, $"[MessageResolver/OnReceived] {e.Message}");
            }
        }

        private bool CopyBuffer(byte[] buffer, ref int startPosition)
        {
            int copySize = _targetPosition - _currentPosition;
            if (copySize < 0 || _remainBytes < 0)
            {
                return false;
            }
            if (_remainBytes < copySize)
            {
                copySize = _remainBytes;
            }

            Array.Copy(buffer, startPosition, _messageBuffer, _currentPosition, copySize);
            startPosition += copySize;
            _currentPosition += copySize;
            _remainBytes -= copySize;

            return _currentPosition >= _targetPosition;
        }

        private int ParseHeader()
        {
            return BitConverter.ToInt32(_messageBuffer, 0);
        }

        private void ClearBuffer()
        {
            Array.Clear(_messageBuffer, 0, _messageBuffer.Length);
            _currentPosition = 0;
            _targetPosition = 0;
        }
    }
}
