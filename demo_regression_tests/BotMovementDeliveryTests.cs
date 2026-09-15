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
    public void SynchronizationBatchesChangedBotAndMonsterForEachRecipient()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44105);
        var recipient = AddRecipient(store, 44105, 1, AreaType.S2Ground, []);
        var elsewhere = AddRecipient(store, 44105, 2, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.CurrentArea = AreaType.S2Ground;
        runtime.Bots.GetBots().Add(bot);
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Ground
        };
        runtime.Monsters.Entities[1] = monster;
        var synchronization = new MatchSynchronizationService();
        synchronization.TrackNewObjects(runtime);
        bot.Player.GameInfo.ObjectInfo.Position = new Vector3f(1, 0, 0);
        monster.Position = new Vector3f(2, 0, 0);
        synchronization.ProcessTick(runtime, now);

        Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, recipient.Packets[0].Protocol);
        Assert.Single(recipient.Packets.Where(packet => packet.Protocol == Protocol.G_TO_C_MOVE));
        Assert.Equal(2, recipient.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects.Count);
        Assert.Empty(elsewhere.Packets);
        recipient.Packets.Clear();
        synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(recipient.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MonsterSnapshotOnlyIncludesCurrentArea(bool started)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44104);
        var recipient = AddRecipient(store, 44104, 1, AreaType.S2Ground, []);
        using var scope = runtime.Enter();
        if (started) runtime.StartGameplay(DateTime.UtcNow);
        runtime.Monsters.Entities[1] = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Ground
        };
        runtime.Monsters.Entities[2] = new game_server.matches.monsters.Monster
        {
            MonsterId = 2, Alive = true, Health = 10, Area = AreaType.S2Library1
        };
        var session = runtime.GetSessions().Single();
        session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.Equal(1, Assert.Single(recipient.Read<G_TO_C_MONSTER_SNAPSHOT>(Protocol.G_TO_C_MONSTER_SNAPSHOT).Monsters).MonsterId);
        recipient.Packets.Clear();
        session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.Empty(recipient.Packets);
    }

    [Fact]
    public void SynchronizationTracksFirstAreaChangeAndSkipsUnchangedTick()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44103);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var oldArea = AddRecipient(store, 44103, 1, AreaType.S2Ground, timeline);
        var newArea = AddRecipient(store, 44103, 2, AreaType.S2Library1, timeline);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.CurrentArea = AreaType.S2Ground;
        runtime.Bots.GetBots().Add(bot);
        var synchronization = new MatchSynchronizationService();
        synchronization.TrackNewObjects(runtime);
        Assert.Empty(oldArea.Packets);
        bot.Player.CurrentArea = AreaType.S2Library1;
        synchronization.ProcessTick(runtime, now);
        Assert.Contains(oldArea.Packets, packet => packet.Protocol == Protocol.G_TO_C_AREA_PLAYER_LEAVE);
        Assert.Equal(Protocol.G_TO_C_AREA_PLAYER_ENTER, newArea.Packets[0].Protocol);
        Assert.Equal(Protocol.G_TO_C_MOVE, newArea.Packets[1].Protocol);
        oldArea.Packets.Clear();
        newArea.Packets.Clear();
        synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(oldArea.Packets);
        Assert.Empty(newArea.Packets);
    }

    [Fact]
    public void MovementOnlySendDoesNotChangeBotStateOrSendSnapshots()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44102);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44102, 1, AreaType.S2Ground, timeline);
        using var scope = runtime.Enter();
        var bot = new Bot { PlayerId = -20 };
        bot.Player.CurrentArea = AreaType.S2Ground;
        bot.Player.State = PlayerState.EXPLORE_1;
        bot.Player.GameInfo.ObjectInfo.Velocity = new Vector3f(1f, 0f, 0f);
        runtime.Bots.GetBots().Add(bot);
        runtime.Monsters.Entities[1] = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Ground
        };

        var message = new G_TO_C_MOVE();
        message.Objects.Add(bot.Player.GameInfo.ObjectInfo.Clone());
        var messages = new Dictionary<GameClientSession, G_TO_C_MOVE>
        {
            [runtime.GetSessions().Single()] = message
        };
        new MatchSynchronizationService().SendMovements(runtime, messages);

        Assert.Equal(PlayerState.EXPLORE_1, bot.Player.State);
        Assert.Equal(Protocol.G_TO_C_MOVE, Assert.Single(recipient.Packets).Protocol);
    }

    private static void CompleteMovements(MatchRuntime runtime, IReadOnlyList<(GameObjectInfo Info, AreaType FromArea)> movements)
    {
        var service = new MatchSynchronizationService();
        foreach (var session in runtime.GetSessions())
            session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        foreach (var movement in movements)
        {
            var bot = runtime.Bots.GetBot(movement.Info.ObjectId);
            if (bot == null) continue;
            runtime.SynchronizedBotStates[bot.PlayerId] = bot.Player.State;
            bot.Player.GameInfo.ObjectInfo.Velocity = movement.Info.Velocity;
            new BotBehaviorService(null!, null!, null!).CompleteMovement(runtime, bot);
        }
        var messages = new Dictionary<GameClientSession, G_TO_C_MOVE>();
        foreach (var movement in movements)
        {
            service.QueueObjectMovement(runtime, movement.Info, movement.FromArea, messages);
        }
        service.SendMovements(runtime, messages);
    }

    private static void SendMovements(MatchRuntime runtime, IReadOnlyList<BotMovementResult> movements)
    {
        var objects = new List<(GameObjectInfo Info, AreaType FromArea)>();
        foreach (var movement in movements)
        {
            objects.Add((new GameObjectInfo
            {
                ObjectType = ObjectType.PLAYER, ObjectId = movement.BotPlayerId,
                Area = movement.ToArea, Cell = movement.ToCell, Position = movement.Position,
                Velocity = movement.Velocity, Rotation = movement.Rotation
            }, movement.FromArea));
        }
        CompleteMovements(runtime, objects);
    }

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
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            SendMovements(match, [new BotMovementResult
            {
                BotPlayerId = -20, FromArea = AreaType.S2Ground, ToArea = AreaType.S2Ground,
                Position = new Vector3f(1, 1, 0), Velocity = new Vector3f(1, 0, 0), ToCell = new Cell(1, 1)
            }]);
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
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            float phase = bot.Player.OrbOrbitPhaseDegrees;
            SendMovements(match, [movement]);
            Assert.Equal(phase, bot.Player.OrbOrbitPhaseDegrees);
        }

        if (changesArea)
        {
            Assert.Equal(
                new[] { (1L, Protocol.G_TO_C_AREA_PLAYER_LEAVE), (2L, Protocol.G_TO_C_AREA_PLAYER_ENTER), (2L, Protocol.G_TO_C_MOVE) },
                timeline);
            var appearance = destination.Read<G_TO_C_AREA_PLAYER_ENTER>(Protocol.G_TO_C_AREA_PLAYER_ENTER);
            Assert.Equal(PlayerState.SLEEP, appearance.GamePlayer.State);
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
        var sent = Assert.Single(destination.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects);
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
        var movement = new BotMovementResult();
        Assert.Throws<InvalidOperationException>(() => SendMovements(match, [movement]));
        using (match.Enter())
        {
            match.TryMarkEnded();
            Assert.Throws<InvalidOperationException>(() => SendMovements(match, [movement]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementAndDamageWaitForSynchronization(bool started)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44005);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44005, 101, AreaType.S2Corridor9, timeline);
        var elsewhere = AddRecipient(store, 44005, 102, AreaType.S2Library1, timeline);
        var otherMatch = AddRecipient(store, 44006, 103, AreaType.S2Corridor9, timeline);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        if (started) runtime.StartGameplay(now);
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Corridor9,
            Position = network.common.data.MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9))
        };
        runtime.Monsters.Entities[1] = monster;
        var movement = new MatchMoveService(null!, new game_server.matches.monsters.MonsterBehaviorService());
        movement.ProcessTick(runtime, now);
        if (!started)
        {
            movement.ProcessTick(runtime, now.AddMilliseconds(50));
            Assert.Empty(recipient.Packets);
            Assert.Empty(elsewhere.Packets);
            Assert.Empty(otherMatch.Packets);
            return;
        }
        Assert.DoesNotContain(recipient.Packets, packet => packet.Protocol == Protocol.G_TO_C_MONSTER_SNAPSHOT);
        foreach (var session in runtime.GetSessions())
            session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.True(Assert.Single(recipient.Read<G_TO_C_MONSTER_SNAPSHOT>(Protocol.G_TO_C_MONSTER_SNAPSHOT).Monsters).IsAlive);
        recipient.Packets.Clear();

        runtime.Monsters.Initialize(now);
        var combat = new game_server.matches.monsters.MonsterCombatService(null!);
        combat.ApplyMonsterDamage(runtime, monster.CombatTargetId, 101, 5, now);
        Assert.Empty(recipient.Packets);
        new MatchSynchronizationService().ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Equal(5, Assert.Single(recipient.Read<G_TO_C_MONSTER_SNAPSHOT>(Protocol.G_TO_C_MONSTER_SNAPSHOT).Monsters).CurrentHealth);
        Assert.Empty(elsewhere.Packets);
        Assert.Empty(otherMatch.Packets);

        recipient.Packets.Clear();
        runtime.RemoveMonster(monster);
        Assert.False(Assert.Single(recipient.Read<G_TO_C_MONSTER_SNAPSHOT>(Protocol.G_TO_C_MONSTER_SNAPSHOT).Monsters).IsAlive);
        Assert.Empty(runtime.Monsters.Entities);
    }
    [Fact]
    public void BotAndMonsterSharePacketAndUnchangedMonsterMetadataIsNotResent()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44100);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44100, 1, AreaType.S2Ground, timeline);
        using var scope = runtime.Enter();
        var bot = new Bot { PlayerId = -20 };
        bot.Player.CurrentArea = AreaType.S2Ground;
        runtime.Bots.GetBots().Add(bot);
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Ground
        };
        runtime.Monsters.Entities[1] = monster;
        var objects = new List<(GameObjectInfo Info, AreaType FromArea)>
        {
            (bot.Player.GameInfo.ObjectInfo, AreaType.S2Ground),
            (monster.Info.ObjectInfo, AreaType.S2Ground)
        };
        CompleteMovements(runtime, objects);
        var packet = recipient.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE);
        Assert.Equal(2, packet.Objects.Count);
        Assert.Equal(ObjectType.PLAYER, packet.Objects[0].ObjectType);
        Assert.Equal(ObjectType.MONSTER, packet.Objects[1].ObjectType);
        Assert.Equal(bot.Player.OrbOrbitPhaseDegrees, packet.OrbPhases[-20]);
        Assert.True(packet.ServerTimestamp > 0);
        Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, recipient.Packets[0].Protocol);
        recipient.Packets.Clear();
        CompleteMovements(runtime, objects);
        Assert.Single(recipient.Packets);
        Assert.Equal(Protocol.G_TO_C_MOVE, recipient.Packets[0].Protocol);
        monster.Health = 4;
        recipient.Packets.Clear();
        CompleteMovements(runtime, objects);
        Assert.Equal(4, Assert.Single(recipient.Read<G_TO_C_MONSTER_SNAPSHOT>(Protocol.G_TO_C_MONSTER_SNAPSHOT).Monsters).CurrentHealth);
    }

    [Fact]
    public void MonsterDepartureReachesOldAreaAndReentryRestoresAppearance()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44101);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var oldArea = AddRecipient(store, 44101, 1, AreaType.S2Ground, timeline);
        var newArea = AddRecipient(store, 44101, 2, AreaType.S2Library1, timeline);
        using var scope = runtime.Enter();
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Area = AreaType.S2Ground
        };
        runtime.Monsters.Entities[1] = monster;
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Ground)]);
        oldArea.Packets.Clear();
        newArea.Packets.Clear();
        monster.Area = AreaType.S2Library1;
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Ground)]);
        Assert.Equal(AreaType.S2Library1, Assert.Single(oldArea.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects).Area);
        Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, newArea.Packets[0].Protocol);
        oldArea.Packets.Clear();
        monster.Area = AreaType.S2Ground;
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Library1)]);
        Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, oldArea.Packets[0].Protocol);
    }

    [Fact]
    public void HumanMovementPacketUsesSameObjectListAndPreservesCorrectionData()
    {
        var info = new GameObjectInfo
        {
            ObjectType = ObjectType.PLAYER, ObjectId = 42, MapId = Config.SWARM_MATCH_MAP,
            Area = AreaType.S2Ground, Cell = new Cell(3, 4), Position = new Vector3f(10, 20, 0),
            Velocity = new Vector3f(1, 2, 0), Rotation = 30
        };
        using var packet = PacketMaker.G_TO_C_MOVE(info, 123456, 45f);
        info.Position.X = 999;
        info.Cell.Y = 999;
        var connection = new RecordingConnection(42, []);
        connection.TrySend(packet);
        var message = connection.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE);
        var sent = Assert.Single(message.Objects);
        Assert.Equal(42, sent.ObjectId);
        Assert.Equal(ObjectType.PLAYER, sent.ObjectType);
        Assert.Equal(10f, sent.Position.X);
        Assert.Equal(4, sent.Cell.Y);
        Assert.Equal(30f, sent.Rotation);
        Assert.Equal(2f, sent.Velocity.Y);
        Assert.Equal(AreaType.S2Ground, sent.Area);
        Assert.Equal(123456, message.ServerTimestamp);
        Assert.Equal(45f, message.OrbPhases[42]);
    }

    [Fact]
    public void CountdownSupplyPublishesNewMonstersWithoutMovementTick()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44200);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44200, 101, AreaType.S2Ground, timeline);
        var otherMatch = AddRecipient(store, 44201, 102, AreaType.S2Ground, timeline);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        var participants = new List<PlayerPositionSnapshot>
        {
            new(101, AreaType.S2Ground, new Vector3f())
        };
        var supply = new game_server.matches.monsters.MatchMonsterSpawnService();
        supply.ProcessSupply(runtime, participants, now, preMatch: true);
        Assert.NotEmpty(runtime.Monsters.Entities);
        Assert.NotEmpty(recipient.Packets);
        Assert.All(recipient.Packets, p => Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, p.Protocol));
        Assert.Empty(otherMatch.Packets);
        recipient.Packets.Clear();
        new MatchMoveService(null!, null!).ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(recipient.Packets);
        supply.ProcessSupply(runtime, participants, now.AddMilliseconds(50), preMatch: true);
        Assert.Empty(recipient.Packets);

        // 생성 후 늦게 입장한 세션은 기존 상태 전체를 한 번 받는다.
        var late = AddRecipient(store, 44200, 103, AreaType.S2Ground, timeline);
        var session = runtime.GetSessions().Single(s => s.PlayerId == 103);
        session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.NotEmpty(late.Packets);
        Assert.All(late.Packets, p => Assert.Equal(Protocol.G_TO_C_MONSTER_SNAPSHOT, p.Protocol));
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
