using System.Net.Sockets;

namespace network.managers
{
    public class SocketAsyncEventArgsManager(int capacity)
    {
        private readonly Stack<SocketAsyncEventArgs> _pool = new Stack<SocketAsyncEventArgs>(capacity);

        public void Push(SocketAsyncEventArgs item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item), "Items added to a SocketAsyncEventArgsManager cannot be null");
            }

            lock (_pool)
            {
                _pool.Push(item);
            }
        }

        public SocketAsyncEventArgs Pop()
        {
            lock (_pool)
            {
                if (_pool.Count == 0)
                {
                    throw new InvalidOperationException("[SocketAsyncEventArgsManager] Pool is empty");
                }

                return _pool.Pop();
            }
        }

        public int Count
        {
            get { return _pool.Count; }
        }
    }
}
