using System.Net.Sockets;

namespace network.managers
{
    internal class BufferManager
    {
        private readonly int _numBytes;
        private readonly int _bufferSize;
        private readonly byte[] _buffer;
        private int _currentIndex = 0;

        public BufferManager(int totalBytes, int bufferSize)
        {
            _numBytes = totalBytes;
            _bufferSize = bufferSize;
            _buffer = new byte[_numBytes];
        }

        public bool SetBuffer(SocketAsyncEventArgs args)
        {
            if ((_numBytes - _bufferSize) < _currentIndex)
            {
                return false;
            }

            args.SetBuffer(_buffer, _currentIndex, _bufferSize);
            _currentIndex += _bufferSize;

            return true;
        }
    }
}
