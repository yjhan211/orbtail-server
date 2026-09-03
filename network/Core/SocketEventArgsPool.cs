using System.Net.Sockets;

namespace network.core;

/// <summary>
///     accept된 소켓이 쓰는 수신·송신 <see cref="SocketAsyncEventArgs"/> 쌍을 서버 시작 때 정원만큼 미리 만들어 두고
///     빌려주는 창고. 버퍼는 큰 배열 하나를 잘라 붙여 pin이 한 번이고, 짝으로만 빌려주며, 정지 중에는 반납을 받지 않고 버린다.
///     버퍼 소유 규칙을 <see cref="NetworkService"/> 밖에 두기 위한 클래스라, 여기 말고는 아무도 버퍼를 만들지 않는다.
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
