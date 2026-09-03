using System.Net.Sockets;

namespace network.core;

/// <summary>
///     TCP 연결에서 사용하는 수신용·송신용 SocketAsyncEventArgs를 미리 만들어 보관한다.
///
///     새 연결이 들어오면 수신용과 송신용 객체를 한 쌍으로 빌려주며,
///     둘 중 하나라도 부족하면 대여하지 않는다.
///
///     연결이 정상적으로 끝나면 객체를 다시 받아 다음 연결에서 재사용한다.
///     서버가 종료 중이면 반환된 객체를 풀에 넣지 않고 폐기한다.
/// </summary>
internal sealed class SocketEventArgsPool
{
    private readonly object _gate = new();
    private readonly SocketAsyncEventArgsManager _receivePool;
    private readonly SocketAsyncEventArgsManager _sendPool;
    private readonly Func<bool> _canReuse;

    public SocketEventArgsPool(
        int capacity,
        int preallocationCount,
        int bufferSize,
        EventHandler<SocketAsyncEventArgs> receiveCompleted,
        EventHandler<SocketAsyncEventArgs> sendCompleted,
        Func<bool> canReuse)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preallocationCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        ArgumentNullException.ThrowIfNull(receiveCompleted);
        ArgumentNullException.ThrowIfNull(sendCompleted);
        ArgumentNullException.ThrowIfNull(canReuse);

        _canReuse = canReuse;
        _receivePool = new SocketAsyncEventArgsManager(capacity);
        _sendPool = new SocketAsyncEventArgsManager(capacity);
        var bufferManager = new BufferManager(
            checked(capacity * preallocationCount * bufferSize),
            bufferSize);

        for (int i = 0; i < capacity; i++)
        {
            SocketAsyncEventArgs receiveArgs = new();
            receiveArgs.Completed += receiveCompleted;
            if (!bufferManager.SetBuffer(receiveArgs))
                throw new InvalidOperationException("The receive buffer pool is smaller than its configured capacity.");
            _receivePool.Push(receiveArgs);

            SocketAsyncEventArgs sendArgs = new();
            sendArgs.Completed += sendCompleted;
            if (!bufferManager.SetBuffer(sendArgs))
                throw new InvalidOperationException("The send buffer pool is smaller than its configured capacity.");
            _sendPool.Push(sendArgs);
        }
    }

    public bool TryRent(
        out SocketAsyncEventArgs? receiveArgs,
        out SocketAsyncEventArgs? sendArgs)
    {
        lock (_gate)
        {
            bool hasReceiveArgs = _receivePool.TryPop(out receiveArgs);
            bool hasSendArgs = _sendPool.TryPop(out sendArgs);
            if (hasReceiveArgs && hasSendArgs)
                return true;

            if (receiveArgs != null)
                _receivePool.Push(receiveArgs);
            if (sendArgs != null)
                _sendPool.Push(sendArgs);
            receiveArgs = null;
            sendArgs = null;
            return false;
        }
    }

    public void Return(
        SocketAsyncEventArgs? receiveArgs,
        SocketAsyncEventArgs? sendArgs)
    {
        if (receiveArgs == null || sendArgs == null)
        {
            receiveArgs?.Dispose();
            sendArgs?.Dispose();
            return;
        }

        receiveArgs.UserToken = null;
        sendArgs.UserToken = null;
        lock (_gate)
        {
            if (_canReuse())
            {
                _receivePool.Push(receiveArgs);
                _sendPool.Push(sendArgs);
                return;
            }
        }

        receiveArgs.Dispose();
        sendArgs.Dispose();
    }

    public void DisposeIdle()
    {
        lock (_gate)
        {
            _receivePool.DisposeAll();
            _sendPool.DisposeAll();
        }
    }
}
