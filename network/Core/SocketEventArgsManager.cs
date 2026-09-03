using System.Net.Sockets;

namespace network.core;

/// <summary>
///     재사용할 SocketAsyncEventArgs를 스택에 보관하는 단순한 객체 풀이다.
///
///     Push로 사용이 끝난 객체를 보관하고,
///     Pop 또는 TryPop으로 다음 연결에서 사용할 객체를 꺼낸다.
///     DisposeAll은 풀에 남아 있는 모든 객체를 폐기한다.
///
///     여러 스레드가 동시에 객체를 넣고 꺼낼 수 있으므로 스택 접근을 잠금으로 보호한다.
/// </summary>
public class SocketAsyncEventArgsManager(int capacity)
{
    private readonly Stack<SocketAsyncEventArgs> _pool = new(capacity);

    public void Push(SocketAsyncEventArgs item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item),
                "Items added to a SocketAsyncEventArgsManager cannot be null");

        lock (_pool)
        {
            _pool.Push(item);
        }
    }

    public SocketAsyncEventArgs Pop()
    {
        lock (_pool)
        {
            if (_pool.Count == 0) throw new InvalidOperationException("[SocketAsyncEventArgsManager] Pool is empty");

            return _pool.Pop();
        }
    }

    public bool TryPop(out SocketAsyncEventArgs? item)
    {
        lock (_pool)
        {
            if (_pool.Count == 0)
            {
                item = null;
                return false;
            }

            item = _pool.Pop();
            return true;
        }
    }

    public void DisposeAll()
    {
        lock (_pool)
        {
            while (_pool.Count > 0)
                _pool.Pop().Dispose();
        }
    }
}
