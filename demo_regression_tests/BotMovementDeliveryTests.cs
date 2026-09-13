using System.Reflection;
using game_server.matches;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.core;
using network.packets;

namespace demo_regression_tests;

public sealed class BotMovementDeliveryTests
{
    [Fact]
    public void MovingBotCancelsDoorAndPublishesIdleBeforeMovement()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(44004);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, match.MatchingId, 1, AreaType.S2Ground, timeline);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.State = PlayerState.EXPLORE_1;
        bot.Player.BeginDoor(213, 0);
        var service = new BotMovementService(NullLogger<BotMovementService>.Instance);
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            service.DispatchExternalMovement(match, new BotMovementResult
            {
                BotPlayerId = -20, FromArea = AreaType.S2Ground, ToArea = AreaType.S2Ground,
                Position = new Vector3f(1, 1, 0), Velocity = new Vector3f(1, 0, 0), ToCell = new Cell(1, 1)
            });
            Assert.Null(bot.Player.PendingDoorInteractionId);
            Assert.Equal(PlayerState.IDLE, bot.Player.State);
        }
        Assert.Equal(new[] { (1L, Protocol.G_TO_C_PLAYER_STATE), (1L, Protocol.G_TO_C_MOVE) }, timeline);
        Assert.Equal(PlayerState.IDLE, recipient.Read<G_TO_C_PLAYER_STATE>(Protocol.G_TO_C_PLAYER_STATE).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovementIsSentToItsAreaInProtocolOrder(bool changesArea)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(44001);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var previous = AddRecipient(store, match.MatchingId, 1, AreaType.S2Gym1, timeline);
        var destination = AddRecipient(store, match.MatchingId, 2, AreaType.S2Ground, timeline);
        var elsewhere = AddRecipient(store, match.MatchingId, 3, AreaType.S2Library1, timeline);
        var otherMatch = AddRecipient(store, 44002, 4, AreaType.S2Ground, timeline);
        var movement = new BotMovementResult
        {
            BotPlayerId = -20,
            FromArea = changesArea ? AreaType.S2Gym1 : AreaType.S2Ground,
            ToArea = AreaType.S2Ground,
            ToCell = new Cell(3, 4),
            Position = new Vector3f(10, 20, 0),
            Velocity = new Vector3f(3, 4, 0),
            Rotation = 17,
            IsAreaTransition = changesArea
        };
        var bot = new Bot { PlayerId = -20 };
        bot.Player.CurrentArea = movement.ToArea;
        bot.Player.Cell = movement.ToCell;
        bot.Player.Position = movement.Position;
        bot.Player.State = PlayerState.SLEEP;
        var service = new BotMovementService(NullLogger<BotMovementService>.Instance);
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            float phase = bot.Player.OrbOrbitPhaseDegrees;
            service.DispatchExternalMovement(match, movement);
            Assert.Equal(phase, bot.Player.OrbOrbitPhaseDegrees);
        }

        if (changesArea)
        {
            Assert.Equal(
                new[] { (1L, Protocol.G_TO_C_AREA_PLAYER_LEAVE), (2L, Protocol.G_TO_C_AREA_PLAYER_ENTER), (2L, Protocol.G_TO_C_MOVE) },
                timeline);
            var appearance = destination.Read<G_TO_C_AREA_PLAYER_ENTER>(Protocol.G_TO_C_AREA_PLAYER_ENTER);
            Assert.Equal(PlayerState.SLEEP, appearance.Player.State);
        }
        else
        {
            Assert.Equal(new[] { (2L, Protocol.G_TO_C_MOVE) }, timeline);
            Assert.Empty(previous.Packets);
        }
        Assert.Empty(elsewhere.Packets);
        Assert.Empty(otherMatch.Packets);

        // 전송 후 원본을 바꿔도 큐에 전달한 직렬화 결과는 바뀌지 않는다.
        movement.Position.X = 999;
        movement.ToCell.Y = 999;
        var sent = destination.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE);
        Assert.Equal(10f, sent.Position.X);
        Assert.Equal(4, sent.Cell.Y);
        Assert.Equal(3f, sent.Velocity.X);
        Assert.Equal(17f, sent.Rotation);
    }

    [Fact]
    public void ExternalMovementRequiresLockAndRejectsEndedMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(44003);
        var service = new BotMovementService(NullLogger<BotMovementService>.Instance);
        var movement = new BotMovementResult();
        Assert.Throws<InvalidOperationException>(() => service.DispatchExternalMovement(match, movement));
        using (match.Enter())
        {
            match.TryMarkEnded();
            Assert.Throws<InvalidOperationException>(() => service.DispatchExternalMovement(match, movement));
        }
    }

    private static RecordingConnection AddRecipient(
        MatchRuntimeStore store, long matchingId, long playerId, AreaType area,
        List<(long PlayerId, Protocol Protocol)> timeline)
    {
        var connection = new RecordingConnection(playerId, timeline);
        var session = TestGameSessionServices.CreateRecipientSession(connection);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(GameClientSession).GetProperty("PlayerId", flags)!.SetValue(session, playerId);
        typeof(GameClientSession).GetProperty("MatchingId", flags)!.SetValue(session, matchingId);
        TestGameSessionServices.BindMatch(session, matchingId, store);
        session.Player.CurrentArea = area;
        TestGameSessionServices.AttachSession(session);
        return connection;
    }

    private sealed class RecordingConnection(long playerId, List<(long PlayerId, Protocol Protocol)> timeline) : TcpConnection
    {
        public List<(Protocol Protocol, byte[] Wire)> Packets { get; } = [];

        public override bool TrySend(Packet packet)
        {
            packet.RecordSize();
            var protocol = (Protocol)packet.ProtocolId;
            Packets.Add((protocol, packet.ToBytes()));
            timeline.Add((playerId, protocol));
            return true;
        }

        public T Read<T>(Protocol protocol)
        {
            var sent = Assert.Single(Packets, packet => packet.Protocol == protocol);
            using var packet = Packet.Create(sent.Wire);
            packet.PopProtocolId();
            packet.PopPlayerId();
            return MessagePackSerializer.Deserialize<T>(packet.PopBody());
        }
    }
}
