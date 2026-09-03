using System.Net.Sockets;
using network.core;
using network.interfaces;
using network.utils;

namespace demo_regression_tests;

/// <summary>
///     UserToken 수명 보장: 종료 경로가 여럿이어도 닫기 시작은 한 번, 진행 중 작업이 0이 돼야 자원 반납,
///     닫기 시작 뒤 새 작업 거절, peer 통보는 각 한 번, ReleaseTask는 반납 뒤에만 완료(초기화 전에는 이미 완료).
///     NetworkService의 역할(닫기 시작 → 전송 닫기·준비 표시, 반납 준비 → peer 통보·args 분리·released)은
///     <see cref="Harness" />가 같은 순서로 흉내 낸다.
/// </summary>
public sealed class UserTokenLifecycleTests
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
                harness.Token.Disconnect();
            else if (harness.Token.TryBeginClose())
                harness.CloseStarted(harness.Token, ConnectionCloseReason.RemoteClosed, null);
        }, TaskCreationOptions.LongRunning)).ToArray();
        start.Set();
        await Task.WhenAll(racers);

        Assert.Equal(1, harness.CloseStartedCount);
        Assert.Equal(1, harness.ReleaseReadyCount);
        Assert.True(harness.Token.IsReleased);
        Assert.True(harness.Token.ReleaseTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Release_WaitsUntilPendingOperationsFinish()
    {
        using var harness = Harness.Create();

        Assert.True(harness.Token.TryBeginOperation());
        harness.Token.Disconnect();

        Assert.Equal(1, harness.CloseStartedCount);
        Assert.Equal(0, harness.ReleaseReadyCount);
        Assert.False(harness.Token.ReleaseTask.IsCompleted);

        harness.Token.CompleteOperation();

        Assert.Equal(1, harness.ReleaseReadyCount);
        Assert.True(harness.Token.ReleaseTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Operations_AreRejectedOnceCloseBegins()
    {
        using var harness = Harness.Create();

        Assert.True(harness.Token.TryBeginClose());
        Assert.False(harness.Token.TryBeginOperation());
        Assert.False(harness.Token.IsAcceptingMessages);
        Assert.False(harness.Token.TryRunIfActive(() => { }));
    }

    [Fact]
    public void PeerCallbacks_FireExactlyOnce()
    {
        using var harness = Harness.Create();

        harness.Token.NotifyPeerClosed(_ => { });
        harness.Token.NotifyPeerClosed(_ => { });

        Assert.Equal(1, harness.Peer.DisconnectCount);
        Assert.Equal(1, harness.Peer.RemovedCount);
    }

    [Fact]
    public void ReleaseTask_IsAlreadyCompleteBeforeInitialization()
    {
        var token = new UserToken();

        Assert.True(token.ReleaseTask.IsCompletedSuccessfully);
        Assert.True(token.IsReleased);
    }

    [Fact]
    public void Initialize_RejectsSecondCall()
    {
        using var harness = Harness.Create();

        Assert.Throws<InvalidOperationException>(() => harness.Initialize(harness.Token));
    }

    [Fact]
    public void Authenticate_OnlyOnceAndOnlyWhileActive()
    {
        using var harness = Harness.Create();

        int callbacks = 0;
        Assert.True(harness.Token.TryMarkAuthenticated(() => callbacks++));
        Assert.False(harness.Token.TryMarkAuthenticated(() => callbacks++));
        Assert.Equal(1, callbacks);

        harness.Token.Disconnect();
        Assert.False(harness.Token.TryMarkAuthenticated());
    }

    [Fact]
    public void TrySend_IsRefusedAfterRelease()
    {
        using var harness = Harness.Create();
        harness.Token.Disconnect();

        using var packet = network.packets.Packet.Create(1);
        Assert.False(harness.Token.TrySend(packet));
    }

    private sealed class FakePeer : IPeer
    {
        public int DisconnectCount;
        public int RemovedCount;

        public Task OnMessageFromClient(Const<byte[]> buffer) => Task.CompletedTask;
        public void OnDisconnect() => Interlocked.Increment(ref DisconnectCount);
        public void OnRemoved() => Interlocked.Increment(ref RemovedCount);
        public void Send(IPacket msg)
        {
        }
    }

    /// <summary>NetworkService가 UserToken에 넘기는 두 콜백을 같은 순서로 재현한다.</summary>
    private sealed class Harness : IDisposable
    {
        private int _closeStarted;
        private int _releaseReady;

        public UserToken Token { get; } = new();
        public FakePeer Peer { get; } = new();
        public int CloseStartedCount => Volatile.Read(ref _closeStarted);
        public int ReleaseReadyCount => Volatile.Read(ref _releaseReady);

        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly SocketAsyncEventArgs _receiveArgs = new();
        private readonly SocketAsyncEventArgs _sendArgs = new();

        public static Harness Create()
        {
            var harness = new Harness();
            harness.Initialize(harness.Token);
            harness.Token.SetPeer(harness.Peer);
            return harness;
        }

        public void Initialize(UserToken token) =>
            token.InitializeConnection(_socket, _receiveArgs, _sendArgs, CloseStarted, ReleaseReady);

        public void CloseStarted(UserToken token, ConnectionCloseReason reason, Exception? exception)
        {
            Interlocked.Increment(ref _closeStarted);
            token.CloseTransport(_ => { });
            token.MarkClosePrepared();
        }

        private void ReleaseReady(UserToken token)
        {
            Interlocked.Increment(ref _releaseReady);
            token.NotifyPeerClosed(_ => { });
            token.DetachEventArgs(out _, out _);
            token.MarkReleased();
        }

        public void Dispose()
        {
            _socket.Dispose();
            _receiveArgs.Dispose();
            _sendArgs.Dispose();
        }
    }
}
