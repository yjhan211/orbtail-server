using System.Net.Sockets;
using network.core;
using network.packets;

namespace server_tests;

/// <summary>
///     TcpConnection 수명 보장: 종료 경로가 여럿이어도 닫기 시작은 한 번, 진행 중 작업이 0이 돼야 자원 반납,
///     닫기 시작 뒤 새 작업 거절, 세션 통보는 각 한 번, ReleaseTask는 반납 뒤에만 완료(초기화 전에는 이미 완료).
///     NetworkService의 역할(닫기 시작 → 전송 닫기·준비 표시, 반납 준비 → 세션 통보·args 분리·released)은
///     <see cref="Harness" />가 같은 순서로 흉내 낸다.
/// </summary>
public sealed class TcpConnectionLifecycleTests
{
    [Fact]
    public async Task Close_StartsOnce_WhenManyPathsRace()
    {
        using var harness = Harness.Create();

        var start = new ManualResetEventSlim();
        Task[] racers = Enumerable.Range(0, 16).Select(i => Task.Factory.StartNew(() =>
        {
            start.Wait();
            if (i % 2 == 0)
                harness.Connection.Disconnect();
            else if (harness.Connection.TryBeginClose())
                harness.CloseStarted(harness.Connection, ConnectionCloseReason.RemoteClosed, null);
        }, TaskCreationOptions.LongRunning)).ToArray();
        start.Set();
        await Task.WhenAll(racers);

        Assert.Equal(1, harness.CloseStartedCount);
        Assert.Equal(1, harness.ReleaseReadyCount);
        Assert.True(harness.Connection.IsReleased);
        Assert.True(harness.Connection.ReleaseTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Release_WaitsUntilPendingOperationsFinish()
    {
        using var harness = Harness.Create();

        Assert.True(harness.Connection.TryBeginOperation());
        harness.Connection.Disconnect();

        Assert.Equal(1, harness.CloseStartedCount);
        Assert.Equal(0, harness.ReleaseReadyCount);
        Assert.False(harness.Connection.ReleaseTask.IsCompleted);

        harness.Connection.CompleteOperation();

        Assert.Equal(1, harness.ReleaseReadyCount);
        Assert.True(harness.Connection.ReleaseTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Operations_AreRejectedOnceCloseBegins()
    {
        using var harness = Harness.Create();

        Assert.True(harness.Connection.TryBeginClose());
        Assert.False(harness.Connection.TryBeginOperation());
        Assert.False(harness.Connection.IsAcceptingMessages);
        Assert.False(harness.Connection.TryRunIfActive(() => { }));
    }

    [Fact]
    public void SessionCallbacks_FireExactlyOnce()
    {
        using var harness = Harness.Create();

        harness.Connection.NotifySessionClosed(_ => { });
        harness.Connection.NotifySessionClosed(_ => { });

        Assert.Equal(1, harness.Session.DisconnectCount);
        Assert.Equal(1, harness.Session.RemovedCount);
    }

    [Fact]
    public void ReleaseTask_IsAlreadyCompleteBeforeInitialization()
    {
        var connection = new TcpConnection();

        Assert.True(connection.ReleaseTask.IsCompletedSuccessfully);
        Assert.True(connection.IsReleased);
    }

    [Fact]
    public void Initialize_RejectsSecondCall()
    {
        using var harness = Harness.Create();

        Assert.Throws<InvalidOperationException>(() => harness.Initialize(harness.Connection));
    }

    [Fact]
    public void Authenticate_OnlyOnceAndOnlyWhileActive()
    {
        using var harness = Harness.Create();

        int callbacks = 0;
        Assert.True(harness.Connection.TryMarkAuthenticated(() => callbacks++));
        Assert.False(harness.Connection.TryMarkAuthenticated(() => callbacks++));
        Assert.Equal(1, callbacks);

        harness.Connection.Disconnect();
        Assert.False(harness.Connection.TryMarkAuthenticated());
    }

    [Fact]
    public void TrySend_IsRefusedAfterRelease()
    {
        using var harness = Harness.Create();
        harness.Connection.Disconnect();

        using var packet = network.packets.Packet.Create(1);
        Assert.False(harness.Connection.TrySend(packet));
    }

    private sealed class FakeConnectionSession : IConnectionSession
    {
        public int DisconnectCount;
        public int RemovedCount;

        public Task OnMessageFromClient(ReadOnlyMemory<byte> buffer) => Task.CompletedTask;
        public void OnDisconnect() => Interlocked.Increment(ref DisconnectCount);
        public void OnRemoved() => Interlocked.Increment(ref RemovedCount);
        public bool TrySend(Packet msg)
        {
            return true;
        }
    }

    /// <summary>NetworkService가 TcpConnection에 넘기는 두 콜백을 같은 순서로 재현한다.</summary>
    private sealed class Harness : IDisposable
    {
        private int _closeStarted;
        private int _releaseReady;

        public TcpConnection Connection { get; } = new();
        public FakeConnectionSession Session { get; } = new();
        public int CloseStartedCount => Volatile.Read(ref _closeStarted);
        public int ReleaseReadyCount => Volatile.Read(ref _releaseReady);

        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly SocketAsyncEventArgs _receiveArgs = new();
        private readonly SocketAsyncEventArgs _sendArgs = new();

        public static Harness Create()
        {
            var harness = new Harness();
            harness.Initialize(harness.Connection);
            harness.Connection.SetSession(harness.Session);
            return harness;
        }

        public void Initialize(TcpConnection connection) =>
            connection.InitializeConnection(_socket, _receiveArgs, _sendArgs, CloseStarted, ReleaseReady);

        public void CloseStarted(TcpConnection connection, ConnectionCloseReason reason, Exception? exception)
        {
            Interlocked.Increment(ref _closeStarted);
            connection.CloseTransport(_ => { });
            connection.MarkClosePrepared();
        }

        private void ReleaseReady(TcpConnection connection)
        {
            Interlocked.Increment(ref _releaseReady);
            connection.NotifySessionClosed(_ => { });
            connection.DetachEventArgs(out _, out _);
            connection.MarkReleased();
        }

        public void Dispose()
        {
            _socket.Dispose();
            _receiveArgs.Dispose();
            _sendArgs.Dispose();
        }
    }
}
