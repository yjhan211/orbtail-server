using network.common;
using network.utils;

namespace network.packets;

/// <summary>
///     TCP 바이트 흐름을 메시지 단위로 조립한다. 연결마다 하나.
///     4바이트 길이 프리픽스를 읽고 그 길이만큼 모일 때까지 여러 번의 수신을 이어 붙인다.
///     조립 버퍼는 I/O 버퍼 크기로 시작해 큰 메시지가 올 때만 MAX_MESSAGE_SIZE까지 자라고,
///     메시지를 넘긴 뒤에는 기본 크기로 돌아온다. 상한을 넘는 길이는 FATAL로 돌려 연결을 끊게 한다.
/// </summary>
internal class MessageResolver
{
    private const int MinimumMessageSize = sizeof(int) + sizeof(long);
    private static readonly int MaximumBodySize = Config.MAX_MESSAGE_SIZE - Config.HEADER_SIZE;

    public delegate void CompleteMessageCallback(Const<byte[]> buffer);

    private byte[] _messageBuffer = new byte[Config.BUFFER_SIZE];
    private int _currentPosition;
    private int _remainBytes;
    private int _startPosition;
    private int _targetPosition;

    public (ErrorCode errorCode, string? errorLog) OnReceived(byte[] buffer, int offset, int transferred,
        CompleteMessageCallback callback)
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
                        // 헤더가 덜 왔음. 다음 수신 기다림
                        return (ErrorCode.SUCCESS, null);

                    int messageSize = ParseHeader();
                    if (messageSize < MinimumMessageSize || messageSize > MaximumBodySize)
                        return (ErrorCode.FATAL, $"[MessageResolver/OnReceived] Invalid message size {messageSize}");

                    _targetPosition += messageSize;
                    if (_messageBuffer.Length < _targetPosition)
                        Array.Resize(ref _messageBuffer, _targetPosition);
                }

                // 메세지 복사
                if (!CopyBuffer(buffer, ref _startPosition))
                    // 메세지가 덜 왔음. 다음 수신 기다림
                    return (ErrorCode.SUCCESS, null);

                // 메시지 처리 전에 버퍼 복사
                byte[] messageBufferCopy = new byte[_targetPosition];
                Array.Copy(_messageBuffer, 0, messageBufferCopy, 0, _targetPosition);

                // 메세지 처리
                callback(new Const<byte[]>(messageBufferCopy));

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
        if (copySize < 0 || _remainBytes < 0) return false;
        if (_remainBytes < copySize) copySize = _remainBytes;

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
        if (_messageBuffer.Length > Config.BUFFER_SIZE)
            _messageBuffer = new byte[Config.BUFFER_SIZE];
        else
            Array.Clear(_messageBuffer, 0, _currentPosition);
        _currentPosition = 0;
        _targetPosition = 0;
    }
}
