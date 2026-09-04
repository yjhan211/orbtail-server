using System.Net.Sockets;
using network.common;
using network.core;
using network.packets;
using network.utils;

namespace demo_regression_tests;

/// <summary>
///     메시지 크기는 I/O 버퍼(BUFFER_SIZE)가 아니라 MAX_MESSAGE_SIZE가 정한다.
///     수신은 여러 번의 수신을 이어 붙여 조립하고, 송신은 I/O 버퍼 크기로 잘라 보낸다.
/// </summary>
public sealed class MessageResolverTests
{
    private static byte[] BuildWire(int bodySize, byte fill)
    {
        byte[] wire = new byte[Config.HEADER_SIZE + bodySize];
        BitConverter.GetBytes(bodySize).CopyTo(wire, 0);
        Array.Fill(wire, fill, Config.HEADER_SIZE, bodySize);
        return wire;
    }

    private static List<byte[]> Feed(MessageResolver resolver, byte[] stream, int receiveSize, out ErrorCode lastError)
    {
        var messages = new List<byte[]>();
        lastError = ErrorCode.SUCCESS;
        for (int offset = 0; offset < stream.Length; offset += receiveSize)
        {
            int transferred = Math.Min(receiveSize, stream.Length - offset);
            (lastError, _) = resolver.OnReceived(stream, offset, transferred, buffer => messages.Add(buffer.Value));
            if (lastError != ErrorCode.SUCCESS) break;
        }

        return messages;
    }

    [Fact]
    public void Resolver_AssemblesMessageLargerThanIoBufferAcrossReceives()
    {
        int bodySize = Config.BUFFER_SIZE * 5 + 123;
        byte[] wire = BuildWire(bodySize, 0xAB);

        List<byte[]> messages = Feed(new MessageResolver(), wire, Config.BUFFER_SIZE, out ErrorCode error);

        Assert.Equal(ErrorCode.SUCCESS, error);
        byte[] message = Assert.Single(messages);
        Assert.Equal(wire, message);
    }

    [Fact]
    public void Resolver_SplitsSeveralMessagesArrivingInOneReceive()
    {
        byte[] first = BuildWire(20, 0x01);
        byte[] second = BuildWire(3000, 0x02);
        byte[] third = BuildWire(12, 0x03);
        byte[] stream = first.Concat(second).Concat(third).ToArray();

        List<byte[]> messages = Feed(new MessageResolver(), stream, stream.Length, out ErrorCode error);

        Assert.Equal(ErrorCode.SUCCESS, error);
        Assert.Equal(new[] { first, second, third }, messages);
    }

    [Fact]
    public void Resolver_RejectsLengthBeyondMaxMessageSize()
    {
        byte[] wire = BuildWire(Config.MAX_MESSAGE_SIZE, 0x00);

        List<byte[]> messages = Feed(new MessageResolver(), wire, Config.BUFFER_SIZE, out ErrorCode error);

        Assert.Equal(ErrorCode.FATAL, error);
        Assert.Empty(messages);
    }

    [Fact]
    public void Packet_GrowsForLargeBodyAndShrinksWhenReturnedToPool()
    {
        byte[] body = new byte[Config.BUFFER_SIZE * 3];
        Array.Fill(body, (byte)0x5C);
        using Packet packet = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT, 7);

        packet.SetBody(body);
        packet.RecordSize();

        Assert.True(packet.Buffer.Length >= packet.Position);
        Assert.Equal(Config.HEADER_SIZE + sizeof(int) + sizeof(long) + body.Length, packet.Position);
        using Packet reloaded = Packet.Create(new Const<byte[]>(packet.ToBytes()));
        Assert.Equal((int)Protocol.G_TO_C_GAME_RESULT, reloaded.PopProtocolId());
        Assert.Equal(7, reloaded.PopPlayerId());
        Assert.Equal(body, reloaded.PopBody());
    }

    [Fact]
    public void SendQueue_StagesLargePacketInIoBufferSlices()
    {
        var queue = new SendQueue();
        using var args = new SocketAsyncEventArgs();
        args.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);
        Packet packet = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
        packet.SetBody(new byte[Config.BUFFER_SIZE * 2 + 100]);
        int wireSize = packet.Position;
        queue.TryEnqueue(packet, static () => false);

        int sent = 0;
        int stages = 0;
        while (queue.TryStageNext(args))
        {
            stages++;
            Assert.True(args.Count <= Config.BUFFER_SIZE);
            sent += args.Count;
            SendQueue.AdvanceResult result = queue.Advance(args.Count);
            if (result == SendQueue.AdvanceResult.Drained) break;
            Assert.Equal(SendQueue.AdvanceResult.Continue, result);
        }

        Assert.Equal(wireSize, sent);
        Assert.Equal(3, stages);
    }
}
