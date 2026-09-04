using network.common;
using network.utils;

namespace network.packets;

/// <summary>
///     TCP로 받은 바이트를 패킷 단위로 조립한다. 연결마다 하나씩 사용한다.
///
///     먼저 4바이트 길이 정보를 읽고, 나머지 데이터가 모두 도착하면
///     완성된 패킷을 복사해 콜백으로 전달한다.
///     덜 도착한 데이터는 다음 수신까지 보관하고,
///     한 번에 여러 패킷이 들어오면 순서대로 분리한다.
///
///     조립 버퍼는 2KB로 시작하며 필요한 경우 전체 패킷 상한인 64KB까지 늘어난다.
///     큰 패킷을 전달한 뒤에는 버퍼를 기본 크기로 줄인다.
///     길이가 허용 범위를 벗어나면 FATAL을 반환하며, 호출 측에서 연결을 종료한다.
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
