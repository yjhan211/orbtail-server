using System.Net.Sockets;

namespace network
{
    internal class BufferManager
    {

        int m_numBytes;                 // the total number of bytes controlled by the buffer pool
        int m_currentIndex;
        int m_bufferSize;

        byte[] m_buffer;                // the underlying byte array maintained by the Buffer Manager

        public BufferManager(int totalBytes, int bufferSize)
        {
            m_numBytes = totalBytes;
            m_currentIndex = 0;
            m_bufferSize = bufferSize;

            m_buffer = new byte[m_numBytes];
        }

        public bool SetBuffer(SocketAsyncEventArgs args)
        {
            if ((m_numBytes - m_bufferSize) < m_currentIndex)
            {
                return false;
            }

            args.SetBuffer(m_buffer, m_currentIndex, m_bufferSize);
            m_currentIndex += m_bufferSize;

            return true;
        }
    }
}
