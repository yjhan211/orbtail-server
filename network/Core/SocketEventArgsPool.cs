using System.Net.Sockets;

namespace network.core;

/// <summary>
///     Owns the preallocated receive/send <see cref="SocketAsyncEventArgs"/> pairs used by accepted sockets.
///     It also creates connection-owned pairs for outbound connector sockets, keeping buffer ownership rules out of
///     <see cref="NetworkService"/>.
/// </summary>
internal sealed class SocketEventArgsPool
{
    private readonly object _gate = new();
    private readonly SocketAsyncEventArgsManager _receivePool;
    private readonly SocketAsyncEventArgsManager _sendPool;
    private readonly int _bufferSize;
    private readonly Func<bool> _canReuse;

    public SocketEventArgsPool(
        int capacity,
        int preallocationCount,
        int bufferSize,
        EventHandler<SocketAsyncEventArgs> receiveCompleted,
        EventHandler<SocketAsyncEventArgs> sendCompleted,
        Func<bool> canReuse)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (preallocationCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(preallocationCount));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        ArgumentNullException.ThrowIfNull(receiveCompleted);
        ArgumentNullException.ThrowIfNull(sendCompleted);
        ArgumentNullException.ThrowIfNull(canReuse);

        _bufferSize = bufferSize;
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

    public SocketAsyncEventArgs CreateConnectionOwned(EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        ArgumentNullException.ThrowIfNull(completedHandler);

        SocketAsyncEventArgs eventArgs = new();
        eventArgs.Completed += completedHandler;
        eventArgs.SetBuffer(new byte[_bufferSize], 0, _bufferSize);
        return eventArgs;
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
