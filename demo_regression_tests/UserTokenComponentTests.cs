using System.Net.Sockets;
using network.core;
using network.packets;

namespace demo_regression_tests;

/// <summary>
///     UserToken에서 떼어낸 두 부품. SendQueue: 상한(128개·256KB)·거절 조건은 잠금 안·부분 전송 오프셋·비우기.
///     ConnectionTimeouts: 인증 창은 인증되면 취소, 유휴 창은 Touch로 미뤄지고 Dispose 뒤에는 아무 콜백도 없다.
/// </summary>
public sealed class UserTokenComponentTests
{
    private static Packet NewPacket(int bodyBytes = 16)
    {
        Packet packet = Packet.Create(1);
        packet.SetBody(new byte[bodyBytes]);
        return packet;
    }

    [Fact]
    public void SendQueue_FirstEnqueueStartsSending_NextOnesQueue()
    {
        var queue = new SendQueue();

        Assert.Equal(SendQueue.EnqueueResult.Started, queue.TryEnqueue(NewPacket(), static () => false));
        Assert.Equal(SendQueue.EnqueueResult.Queued, queue.TryEnqueue(NewPacket(), static () => false));
        Assert.Equal(2, queue.Count);
        queue.Clear();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void SendQueue_RefusesInsideTheLockAndOverflowsAtTheCap()
    {
        var queue = new SendQueue();

        Assert.Equal(SendQueue.EnqueueResult.Refused, queue.TryEnqueue(NewPacket(), static () => true));
        Assert.Equal(0, queue.Count);

        for (int i = 0; i < SendQueue.MaxQueuedPackets; i++)
            Assert.NotEqual(SendQueue.EnqueueResult.Overflow, queue.TryEnqueue(NewPacket(), static () => false));
        Assert.Equal(SendQueue.EnqueueResult.Overflow, queue.TryEnqueue(NewPacket(), static () => false));
        queue.Clear();
    }

    [Fact]
    public void SendQueue_HoldsExactly128MaxSizePackets()
    {
        // 바이트 상한(256KB)은 최대 크기 패킷(BUFFER_SIZE) 128개와 정확히 같다 — 두 상한이 같은 자리에서 만난다.
        var queue = new SendQueue();
        int maxBody = network.common.Config.BUFFER_SIZE - 16;

        for (int i = 0; i < SendQueue.MaxQueuedPackets; i++)
            Assert.NotEqual(SendQueue.EnqueueResult.Overflow, queue.TryEnqueue(NewPacket(maxBody), static () => false));
        Assert.Equal(SendQueue.MaxQueuedPackets, queue.Count);
        Assert.Equal(SendQueue.EnqueueResult.Overflow, queue.TryEnqueue(NewPacket(1), static () => false));
        queue.Clear();
    }

    [Fact]
    public void SendQueue_StagesRemainingBytesAndAdvancesThroughPartialSends()
    {
        var queue = new SendQueue();
        using var args = new SocketAsyncEventArgs();
        args.SetBuffer(new byte[network.common.Config.BUFFER_SIZE], 0, network.common.Config.BUFFER_SIZE);
        Packet packet = NewPacket(100);
        int wireSize = packet.Position;
        queue.TryEnqueue(packet, static () => false);
        queue.TryEnqueue(NewPacket(8), static () => false);

        Assert.True(queue.TryStageNext(args));
        Assert.Equal(wireSize, args.Count);

        Assert.Equal(SendQueue.AdvanceResult.Continue, queue.Advance(wireSize - 10));
        Assert.True(queue.TryStageNext(args));
        Assert.Equal(10, args.Count);

        Assert.Equal(SendQueue.AdvanceResult.NextPacket, queue.Advance(10));
        Assert.True(queue.TryStageNext(args));
        Assert.Equal(SendQueue.AdvanceResult.Drained, queue.Advance(args.Count));
        Assert.False(queue.TryStageNext(args));
        Assert.Equal(SendQueue.AdvanceResult.Invalid, queue.Advance(1));
    }

    [Fact]
    public async Task Timeouts_AuthenticationWindowFiresUnlessAuthenticated()
    {
        using var fired = new ConnectionTimeouts(
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        using var cancelled = new ConnectionTimeouts(
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        int firedCount = 0;
        int cancelledCount = 0;

        fired.StartAuthenticationWindow(() => Interlocked.Increment(ref firedCount));
        cancelled.StartAuthenticationWindow(() => Interlocked.Increment(ref cancelledCount));
        cancelled.MarkAuthenticated(() => { });

        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref firedCount));
        Assert.Equal(0, Volatile.Read(ref cancelledCount));
    }

    [Fact]
    public async Task Timeouts_TouchPostponesIdleAndDisposeSilencesEverything()
    {
        using var idle = new ConnectionTimeouts(
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(10));
        var disposed = new ConnectionTimeouts(
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        int idleCount = 0;
        int disposedCount = 0;

        idle.MarkAuthenticated(() => Interlocked.Increment(ref idleCount));
        disposed.StartAuthenticationWindow(() => Interlocked.Increment(ref disposedCount));
        disposed.MarkAuthenticated(() => Interlocked.Increment(ref disposedCount));
        disposed.ArmGracefulClose(() => Interlocked.Increment(ref disposedCount));
        disposed.Dispose();

        // 마감이 지나기 전에 계속 미루면 울리지 않는다
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(60);
            idle.Touch();
        }

        Assert.Equal(0, Volatile.Read(ref idleCount));

        // 미루기를 멈추면 마감 뒤에 한 번 울린다
        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref idleCount));
        Assert.Equal(0, Volatile.Read(ref disposedCount));
    }
}
