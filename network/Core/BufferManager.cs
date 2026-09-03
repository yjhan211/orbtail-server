using System.Net.Sockets;

namespace network.core;
/// <summary>
///     서버 시작 시 큰 바이트 배열 하나를 만들고,
///     수신용·송신용 SocketAsyncEventArgs마다 겹치지 않는 고정 크기 영역을 배정한다.
/// </summary>
internal class BufferManager
{
    private readonly byte[] _buffer;
    private readonly int _bufferSize;
    private readonly int _numBytes;
    private int _currentIndex;

    public BufferManager(int totalBytes, int bufferSize)
    {
        _numBytes = totalBytes;
        _bufferSize = bufferSize;
        _buffer = new byte[_numBytes];
    }

    public bool SetBuffer(SocketAsyncEventArgs args)
    {
        if (_numBytes - _bufferSize < _currentIndex) return false;

        args.SetBuffer(_buffer, _currentIndex, _bufferSize);
        _currentIndex += _bufferSize;
        return true;
    }
}
