using network.common.data;
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
    public void GroundItemUsesTypedEntryLeaveAndDoesNotReplayLandingOnReentry()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44911);
        var recipient = AddRecipient(store, 44911, 1, AreaType.S2Gym1, []);
        var elsewhere = AddRecipient(store, 44911, 2, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        var spawn = TestMapPosition.In(AreaType.S2Gym1);
        var item = Assert.Single(runtime.GroundItems.SpawnItems(AreaType.S2Gym1, spawn.X, spawn.Y, [Config.SUMMON_STONE_GROUND_ITEM_ID]));
        Assert.Empty(recipient.Packets);
        var service = new MatchSynchronizationService();
        var batch = new MatchSynchronizationService.SyncBatch(now, runtime.GetSessions());
        service.CollectGroundItemEntries(runtime, batch);
        Assert.Empty(recipient.Packets);
        Assert.Empty(elsewhere.Packets);
        service.SendBatch(runtime, batch);
        Assert.True(Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Items).IsLanding);
        Assert.Empty(elsewhere.Packets);
        recipient.Packets.Clear();
        service.ProcessTick(runtime, now);
        Assert.Empty(recipient.Packets);

        runtime.GetPlayer(1)!.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        service.ProcessTick(runtime, now);
        var leave = Assert.Single(recipient.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects);
        Assert.Equal(ObjectType.ITEM, leave.Type);
        Assert.Equal(item.GroundItemUid, leave.Id);
        recipient.Packets.Clear();
        TestGroundItemLanding.Complete(runtime.GroundItems);
        runtime.GetPlayer(1)!.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        service.ProcessTick(runtime, now);
        Assert.False(Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Items).IsLanding);
        recipient.Packets.Clear();
        runtime.GroundItems.TakeItem(item.GroundItemUid);
        Assert.Empty(recipient.Packets);
        service.ProcessTick(runtime, now);
        Assert.Equal(item.GroundItemUid, Assert.Single(recipient.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects).Id);
    }

    [Fact]
    public void GroundItemConsumedBeforeFirstPublicationDoesNotSendEntryOrLeave()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44912);
        var recipient = AddRecipient(store, 44912, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var item = Assert.Single(runtime.GroundItems.SpawnItems(AreaType.S2Gym1, 0, 0, [Config.SUMMON_STONE_GROUND_ITEM_ID]));
        runtime.GroundItems.TakeItem(item.GroundItemUid);
        new MatchSynchronizationService().ProcessTick(runtime, DateTime.UtcNow);
        Assert.Empty(recipient.Packets);
    }

    [Fact]
    public void DoorChangesAreCollectedDetachedAndFailedPublicationIsRetried()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44913);
        var recipient = AddRecipient(store, 44913, 1, AreaType.S2Corridor9, []);
        using var scope = runtime.Enter();
        var session = runtime.GetSessions().Single();
        runtime.Doors.OpenDoor(201);
        var service = new MatchSynchronizationService();
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        service.CollectInteractableUpdates(runtime, batch);
        Assert.Empty(recipient.Packets);
        Assert.Contains(batch.InteractableUpdates[session], info => GameInteractableData.Get(info.InteractId)?.DoorId == 201 && info.IsCompleted);
        recipient.AcceptPackets = false;
        service.SendBatch(runtime, batch);
        Assert.DoesNotContain(201, session.PublishedOpenDoors);
        recipient.AcceptPackets = true;
        service.ProcessTick(runtime, DateTime.UtcNow);
        Assert.Contains(recipient.Read<G_TO_C_INTERACTABLE_INFO>(Protocol.G_TO_C_INTERACTABLE_INFO).Objects, info => GameInteractableData.Get(info.InteractId)?.DoorId == 201 && info.IsCompleted);
        Assert.Contains(201, session.PublishedOpenDoors);
        recipient.Packets.Clear();
        service.ProcessTick(runtime, DateTime.UtcNow);
        Assert.Empty(recipient.Packets);

        // 수집한 열린 상태를 보낸 뒤 닫힘은 다음 수집에서 별도로 전송한다.
        var closeBatch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        runtime.Doors.Clear();
        service.CollectInteractableUpdates(runtime, closeBatch);
        Assert.All(closeBatch.InteractableUpdates[session], info => Assert.False(info.IsCompleted));
        Assert.Empty(recipient.Packets);
        service.SendBatch(runtime, closeBatch);
        Assert.All(recipient.Read<G_TO_C_INTERACTABLE_INFO>(Protocol.G_TO_C_INTERACTABLE_INFO).Objects, info => Assert.False(info.IsCompleted));
        Assert.DoesNotContain(201, session.PublishedOpenDoors);
    }

    [Fact]
    public void RemovedMonsterPublishesTypedLeaveOnceWithoutMovement()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44901);
        var recipient = AddRecipient(store, 44901, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        // 사람과 같은 숫자 ID여도 객체 종류로 구분한다.
        runtime.Monsters.Entities[1] = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        var service = new MatchSynchronizationService();
        service.ProcessTick(runtime, now);
        Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Monsters);
        recipient.Packets.Clear();
        runtime.Monsters.Entities.Remove(1);
        service.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Single(recipient.Packets);
        var leave = Assert.Single(recipient.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects);
        Assert.Equal(ObjectType.MONSTER, leave.Type);
        Assert.Equal(1, leave.Id);
        recipient.Packets.Clear();
        service.ProcessTick(runtime, now.AddMilliseconds(100));
        Assert.Empty(recipient.Packets);
    }

    [Fact]
    public void ObserverAreaChangePublishesStationaryMonsterLeaveAndEntry()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44902);
        var recipient = AddRecipient(store, 44902, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        runtime.Monsters.Entities[11] = new game_server.matches.monsters.Monster
        {
            MonsterId = 11, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[12] = new game_server.matches.monsters.Monster
        {
            MonsterId = 12, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)))
        };
        var service = new MatchSynchronizationService();
        service.ProcessTick(runtime, now);
        recipient.Packets.Clear();
        runtime.GetPlayer(1)!.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        service.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Equal(11, Assert.Single(recipient.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects).Id);
        Assert.Equal(12, Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Monsters).MonsterId);
        Assert.Equal(1, Assert.Single(recipient.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects).ObjectId);
    }

    [Fact]
    public void RejectedEntryIsRetriedAndRejectedLeaveKeepsPublishedIdentity()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44903);
        var recipient = AddRecipient(store, 44903, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var session = runtime.GetSessions().Single();
        var entries = new G_TO_C_OBJECT_ENTER
        {
            Monsters = [new MonsterInfo { MonsterId = 7, IsAlive = true }]
        };
        recipient.AcceptPackets = false;
        session.SendObjectEntries(entries);
        Assert.Empty(session.PublishedObjects);
        recipient.AcceptPackets = true;
        session.SendObjectEntries(entries);
        Assert.Contains((ObjectType.MONSTER, 7L), session.PublishedObjects);
        recipient.AcceptPackets = false;
        session.SendObjectLeaves([new ObjectIdentity { Type = ObjectType.MONSTER, Id = 7 }]);
        Assert.Contains((ObjectType.MONSTER, 7L), session.PublishedObjects);
        recipient.AcceptPackets = true;
        session.SendObjectLeaves([new ObjectIdentity { Type = ObjectType.MONSTER, Id = 7 }]);
        Assert.Empty(session.PublishedObjects);
    }

    [Fact]
    public void MovementSnapshotCopiesCoordinatesAndPreservesComparisonTolerance()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var info = new GameObjectInfo { MapId = network.common.Config.SWARM_MATCH_MAP, Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)) };
        var previous = new MatchObjectSnapshot(info);
        Assert.True(previous.Matches(info));
        info.Position.X = 0.000001f;
        Assert.True(previous.Matches(info));
        info.Position.X = 1f;
        Assert.False(previous.Matches(info));
        info.Position.X = 0f;
        info.Velocity.Y = 1f;
        Assert.False(previous.Matches(info));
        info.Velocity.Y = 0f;
        info.Rotation = 1f;
        Assert.False(previous.Matches(info));
        info.Rotation = 0f;
        info.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1));
        Assert.Equal(AreaType.S2Gym1, previous.Area);
        Assert.False(previous.Matches(info));
    }

    [Fact]
    public void MovementBatchSharesOneDetachedSnapshotAcrossRecipients()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44120);
        var first = AddRecipient(store, 44120, 1, AreaType.S2Gym1, []);
        var second = AddRecipient(store, 44120, 2, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var batch = new MatchSynchronizationService.SyncBatch(now, runtime.GetSessions());
        var info = new GameObjectInfo
        {
            MapId = network.common.Config.SWARM_MATCH_MAP, ObjectType = ObjectType.MONSTER, ObjectId = 7,
            Position = TestMapPosition.In(AreaType.S2Gym1), Cell = network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1), Velocity = new Vector3f(1, 0, 0)
        };
        var service = new MatchSynchronizationService();
        service.CollectMovementUpdates(runtime, info, batch);
        Assert.Equal(2, batch.Moves.Count);
        var snapshots = batch.Moves.Values.Select(message => Assert.Single(message.Objects)).ToArray();
        Assert.Same(snapshots[0], snapshots[1]);
        Assert.NotSame(info, snapshots[0]);
        info.Position.X = 999;
        info.Cell.Y = 999;
        info.Velocity.X = 999;
        Assert.Equal(TestMapPosition.In(AreaType.S2Gym1).X, snapshots[0].Position.X);
        Assert.Equal(network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1).Y, snapshots[0].Cell.Y);
        Assert.Equal(1f, snapshots[0].Velocity.X);
        service.SendBatch(runtime, batch);
        long timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        Assert.Equal(timestamp, first.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).ServerTimestamp);
        Assert.Equal(timestamp, second.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).ServerTimestamp);
    }

    [Fact]
    public void TickMovementUsesProvidedTimeAndStopsResendingUnchangedObjects()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44121);
        var observer = AddRecipient(store, 44121, 1, AreaType.S2Gym1, []);
        AddRecipient(store, 44121, 2, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        runtime.StartGameplay(now);
        var service = new MatchSynchronizationService();
        service.InitializeComparisonSnapshots(runtime);
        runtime.GetPlayer(2)!.GameInfo.ObjectInfo.Position.X += 1f;
        service.ProcessTick(runtime, now.AddMilliseconds(50));
        var move = observer.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE);
        Assert.Equal(new DateTimeOffset(now.AddMilliseconds(50)).ToUnixTimeMilliseconds(), move.ServerTimestamp);
        observer.Packets.Clear();
        service.ProcessTick(runtime, now.AddMilliseconds(100));
        Assert.Empty(observer.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatHitsKeepEveryEventUntilTickEndOrTerminalRelease(bool ended)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44111);
        var recipient = AddRecipient(store, 44111, 1, AreaType.S2Gym1, []);
        var other = AddRecipient(store, 44111, 2, AreaType.S2Gym1, []);
        using (runtime.Enter())
        {
            var now = DateTime.UtcNow;
            runtime.StartGameplay(now);
            var synchronization = new MatchSynchronizationService();
            synchronization.InitializeComparisonSnapshots(runtime);
            var player = runtime.GetPlayer(1)!;
            var combat = TestGameSessionServices.CreateCombatDamageService();
            combat.QueuePlayerHitNotification(runtime, player, 2, AreaType.S2Gym1, 123, 5, 95);
            if (ended) runtime.TryMarkEnded();
            combat.QueuePlayerHitNotification(runtime, player, 2, AreaType.S2Gym1, 123, 7, 88);
            Assert.Empty(recipient.Packets);
            Assert.Equal(2, runtime.PendingCombatHits.Count);
            if (!ended)
            {
                synchronization.ProcessTick(runtime, now);
                Assert.Empty(runtime.PendingCombatHits);
                synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
            }
        }
        Assert.Empty(runtime.PendingCombatHits);
        Assert.Equal(2, recipient.Packets.Count);
        var hits = new List<G_TO_C_COMBAT_HIT>();
        foreach (var sent in recipient.Packets)
        {
            Assert.Equal(Protocol.G_TO_C_COMBAT_HIT, sent.Protocol);
            using var packet = Packet.Create(sent.Wire);
            packet.PopProtocolId();
            packet.PopPlayerId();
            hits.Add(MessagePackSerializer.Deserialize<G_TO_C_COMBAT_HIT>(packet.PopBody()));
        }
        Assert.Equal(new[] { 5, 7 }, hits.Select(hit => hit.Damage));
        Assert.Equal(new[] { 95, 88 }, hits.Select(hit => hit.TargetHealth));
        Assert.Empty(other.Packets);
    }

    [Fact]
    public void PlayerBatchSharesIndependentModelAndSendsOnePacketPerRecipient()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44112);
        var first = AddRecipient(store, 44112, 1, AreaType.S2Gym1, []);
        var second = AddRecipient(store, 44112, 2, AreaType.S2Gym1, []);
        var elsewhere = AddRecipient(store, 44112, 3, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        var service = new MatchSynchronizationService();
        foreach (long id in new[] { -10L, -20L })
        {
            var bot = new Bot { PlayerId = id };
            bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
            bot.Player.GameInfo.Name = $"Bot{id}";
            bot.Player.GameInfo.WearItemIdList.Add(101000003);
            bot.Player.State = PlayerState.SLEEP;
            service.CollectPlayerUpdates(runtime, bot.Player, batch);
            bot.Player.GameInfo.WearItemIdList.Clear();
            bot.Player.State = PlayerState.IDLE;
        }
        Assert.Equal(2, batch.Entries.Count);
        var models = batch.Entries.Values.Select(entry => entry.Players).ToArray();
        Assert.Same(models[0][0], models[1][0]);
        Assert.Single(models[0][0].WearItemIdList);
        Assert.Equal(PlayerState.SLEEP, models[0][0].State);
        Assert.Empty(batch.Leaves);
        Assert.Empty(first.Packets);
        Assert.Empty(second.Packets);
        service.SendBatch(runtime, batch);
        Assert.Single(first.Packets, p => p.Protocol == Protocol.G_TO_C_OBJECT_ENTER);
        Assert.Equal(2, first.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Players.Count);
        Assert.Single(second.Packets, p => p.Protocol == Protocol.G_TO_C_OBJECT_ENTER);
        Assert.Empty(elsewhere.Packets);
    }
    [Fact]
    public void AreaSnapshotIsCollectedWithoutSendingAndPublishedOnce()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44110);
        var recipient = AddRecipient(store, 44110, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var player = runtime.GetPlayer(1)!;
        new MatchSynchronizationService().InitializeComparisonSnapshots(runtime);
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        var areaSnapshots = batch.InteractableUpdates;
        var messages = batch.Moves;
        var monsters = batch.MonsterUpdates;
        var players = batch.PlayerUpdates;
        var service = new MatchSynchronizationService();
        for (int i = 0; i < 2; i++)
        {
            service.CollectInteractableUpdates(runtime, batch);
        }
        Assert.Single(areaSnapshots);
        Assert.Empty(recipient.Packets);
        service.SendBatch(runtime, batch);
        Assert.Single(recipient.Packets, packet => packet.Protocol == Protocol.G_TO_C_INTERACTABLE_INFO);
        Assert.Empty(messages);
    }

    [Fact]
    public void HumanMovementAndAreaVisibilityArePublishedAtTickEnd()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44109);
        var moving = AddRecipient(store, 44109, 1, AreaType.S2Gym1, []);
        var previousArea = AddRecipient(store, 44109, 2, AreaType.S2Gym1, []);
        var nextArea = AddRecipient(store, 44109, 3, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        foreach (var participant in runtime.GetAlivePlayers())
        {
        }
        var synchronization = new MatchSynchronizationService();
        synchronization.InitializeComparisonSnapshots(runtime);
        var player = runtime.GetPlayer(1)!;
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        Assert.Empty(previousArea.Packets);
        Assert.Empty(nextArea.Packets);

        synchronization.ProcessTick(runtime, now);
        Assert.Equal(1, Assert.Single(previousArea.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects).Id);
        Assert.Equal(1, Assert.Single(nextArea.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Players).ObjectInfo.ObjectId);
        Assert.Equal(1, Assert.Single(nextArea.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects).ObjectId);
        Assert.Equal(2, Assert.Single(moving.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects).Id);
        Assert.Equal(3, Assert.Single(moving.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Players).ObjectInfo.ObjectId);
        Assert.Equal(1, Assert.Single(moving.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects).ObjectId);
        previousArea.Packets.Clear();
        nextArea.Packets.Clear();
        moving.Packets.Clear();
        synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(previousArea.Packets);
        Assert.Empty(nextArea.Packets);
        Assert.Empty(moving.Packets);
    }

    [Fact]
    public void HumanStateIsSentOnceAtTickEndWithoutRepeatingMovement()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44108);
        var recipient = AddRecipient(store, 44108, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        var player = runtime.GetPlayer(1)!;
        player.State = PlayerState.SLEEP;
        player.Position = new Vector3f(1, 0, 0);
        var synchronization = new MatchSynchronizationService();
        synchronization.InitializeComparisonSnapshots(runtime);
        Assert.Empty(recipient.Packets);

        synchronization.ProcessTick(runtime, now);
        Assert.Equal(new[] { Protocol.G_TO_C_PLAYER_INFO, Protocol.G_TO_C_MOVE }, recipient.Packets.Select(p => p.Protocol));
        Assert.Equal(PlayerState.SLEEP, Assert.Single(recipient.Read<G_TO_C_PLAYER_INFO>(Protocol.G_TO_C_PLAYER_INFO).Players).State);
        recipient.Packets.Clear();
        synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(recipient.Packets);
    }

    [Fact]
    public void ObjectSynchronizationDefersBotAndMonsterPacketsUntilSendUpdates()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44107);
        var recipient = AddRecipient(store, 44107, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        runtime.Bots.GetBots().Add(bot);
        runtime.RegisterPlayer(bot.Player);
        bot.Player.State = PlayerState.SLEEP;
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[1] = monster;
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        var areaSnapshots = batch.InteractableUpdates;
        var messages = batch.Moves;
        var monsters = batch.MonsterUpdates;
        var bots = batch.PlayerUpdates;
        var service = new MatchSynchronizationService();
        service.CollectPlayerUpdates(runtime, bot.Player, batch);
        service.CollectMonsterUpdates(runtime, monster, batch);
        service.CollectMovementUpdates(runtime, monster.Info.ObjectInfo, batch);
        Assert.Empty(recipient.Packets);

        bot.Player.State = PlayerState.IDLE;
        monster.Health = 1;
        service.SendBatch(runtime, batch);
        Assert.Equal(new[] { Protocol.G_TO_C_OBJECT_ENTER, Protocol.G_TO_C_MOVE },
            recipient.Packets.Select(packet => packet.Protocol));
        Assert.Equal(PlayerState.SLEEP, Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Players).State);
        Assert.Equal(10, Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Monsters).CurrentHealth);
        Assert.Equal(2, recipient.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE).Objects.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationaryMonsterChangesAreBatchedWithoutMovement(bool started)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44106);
        var recipient = AddRecipient(store, 44106, 1, AreaType.S2Gym1, []);
        var elsewhere = AddRecipient(store, 44106, 2, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        if (started) runtime.StartGameplay(now);
        for (int id = 1; id <= 2; id++)
        {
            runtime.Monsters.Entities[id] = new game_server.matches.monsters.Monster
            {
                MonsterId = id, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
            };
        }
        var synchronization = new MatchSynchronizationService();
        synchronization.InitializeComparisonSnapshots(runtime);
        synchronization.ProcessTick(runtime, now);
        Assert.Single(recipient.Packets);
        recipient.Packets.Clear();
        runtime.Monsters.Entities[1].Health = 5;
        runtime.Monsters.Entities[2].ChaseTargetPlayerId = 1;
        synchronization.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Equal(Protocol.G_TO_C_MONSTER_INFO, Assert.Single(recipient.Packets).Protocol);
        var monsters = recipient.Read<G_TO_C_MONSTER_INFO>(Protocol.G_TO_C_MONSTER_INFO).Monsters;
        Assert.Equal(2, monsters.Count);
        Assert.Equal(5, monsters.Single(monster => monster.MonsterId == 1).CurrentHealth);
        Assert.Equal(1, monsters.Single(monster => monster.MonsterId == 2).ChaseTargetPlayerId);
        Assert.Empty(elsewhere.Packets);
        recipient.Packets.Clear();
        synchronization.ProcessTick(runtime, now.AddMilliseconds(100));
        Assert.Empty(recipient.Packets);
    }

    [Fact]
    public void SynchronizationBatchesChangedBotAndMonsterForEachRecipient()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44105);
        var recipient = AddRecipient(store, 44105, 1, AreaType.S2Gym1, []);
        var elsewhere = AddRecipient(store, 44105, 2, AreaType.S2Library1, []);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        runtime.Bots.GetBots().Add(bot);
        runtime.RegisterPlayer(bot.Player);
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[1] = monster;
        var synchronization = new MatchSynchronizationService();
        synchronization.InitializeComparisonSnapshots(runtime);
        bot.Player.Position = TestMapPosition.In(AreaType.S2Gym1, 0.1f);
        monster.Position = TestMapPosition.In(AreaType.S2Gym1, 0.2f);
        synchronization.ProcessTick(runtime, now);

        Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, recipient.Packets[0].Protocol);
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
        var recipient = AddRecipient(store, 44104, 1, AreaType.S2Gym1, []);
        using var scope = runtime.Enter();
        if (started) runtime.StartGameplay(DateTime.UtcNow);
        runtime.Monsters.Entities[1] = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[2] = new game_server.matches.monsters.Monster
        {
            MonsterId = 2, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)))
        };
        var session = runtime.GetSessions().Single();
        session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.Equal(1, Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Monsters).MonsterId);
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
        var oldArea = AddRecipient(store, 44103, 1, AreaType.S2Gym1, timeline);
        var newArea = AddRecipient(store, 44103, 2, AreaType.S2Library1, timeline);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        runtime.Bots.GetBots().Add(bot);
        runtime.RegisterPlayer(bot.Player);
        var synchronization = new MatchSynchronizationService();
        synchronization.InitializeComparisonSnapshots(runtime);
        runtime.GetSessions().Single(s => s.PlayerId == 1).PublishedObjects.Add((ObjectType.PLAYER, bot.PlayerId));
        Assert.Empty(oldArea.Packets);
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        synchronization.ProcessTick(runtime, now);
        Assert.Contains(oldArea.Packets, packet => packet.Protocol == Protocol.G_TO_C_OBJECT_LEAVE);
        Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, newArea.Packets[0].Protocol);
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
        var recipient = AddRecipient(store, 44102, 1, AreaType.S2Gym1, timeline);
        using var scope = runtime.Enter();
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        bot.Player.State = PlayerState.EXPLORE_1;
        bot.Player.GameInfo.ObjectInfo.Velocity = new Vector3f(1f, 0f, 0f);
        runtime.Bots.GetBots().Add(bot);
        runtime.RegisterPlayer(bot.Player);
        runtime.Monsters.Entities[1] = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };

        var message = new G_TO_C_MOVE();
        message.Objects.Add(bot.Player.GameInfo.ObjectInfo.Clone());
        var messages = new Dictionary<GameClientSession, G_TO_C_MOVE>
        {
            [runtime.GetSessions().Single()] = message
        };
        new MatchSynchronizationService().SendBatch(runtime, new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions()) { Moves = messages });

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
            runtime.SynchronizedPlayerStates[bot.PlayerId] = bot.Player.State;
            bot.Player.GameInfo.ObjectInfo.Velocity = movement.Info.Velocity;
            game_server.players.PlayerMovementService.CompleteMovement(runtime, bot.Player);
        }
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, runtime.GetSessions());
        var areaSnapshots = batch.InteractableUpdates;
        var messages = batch.Moves;
        var monsterUpdates = batch.MonsterUpdates;
        var botUpdates = batch.PlayerUpdates;
        foreach (var movement in movements)
        {
            // 이 도우미의 입력은 이미 변경된 이동 목록이다. 이전 좌표와 다른 값으로 기준을 구성한다.
            var previous = movement.Info.Clone();
            previous.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(movement.FromArea));
            previous.Position.X -= 1f;
            runtime.SynchronizedObjects[(movement.Info.ObjectType, movement.Info.ObjectId)] = new MatchObjectSnapshot(previous);
            if (movement.Info.ObjectType == ObjectType.PLAYER)
            {
                // 이미 출발 구역에 등장한 플레이어의 이동을 준비한다.
                foreach (var session in runtime.GetSessions())
                    if (session.Player.GameInfo.ObjectInfo.Area == movement.FromArea)
                        session.PublishedObjects.Add((ObjectType.PLAYER, movement.Info.ObjectId));
                var player = runtime.GetPlayer(movement.Info.ObjectId)!;
                player.GameInfo.ObjectInfo = movement.Info;
                service.CollectPlayerUpdates(runtime, player, batch);
            }
            else
            {
                var monster = runtime.Monsters.Entities[(int)movement.Info.ObjectId];
                service.CollectMonsterUpdates(runtime, monster, batch);
                service.CollectMovementUpdates(runtime, movement.Info, batch);
            }
        }
        service.CollectObjectLeaves(runtime, batch);
        service.SendBatch(runtime, batch);
    }

    private static void SendMovements(MatchRuntime runtime, IReadOnlyList<BotMovementResult> movements)
    {
        var objects = new List<(GameObjectInfo Info, AreaType FromArea)>();
        foreach (var movement in movements)
        {
            objects.Add((new GameObjectInfo
            {
            MapId = network.common.Config.SWARM_MATCH_MAP, ObjectType = ObjectType.PLAYER, ObjectId = movement.BotPlayerId,
                 Cell = movement.ToCell, Position = movement.Position,
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
        var recipient = AddRecipient(store, match.MatchingId, 1, AreaType.S2Gym1, timeline);
        var bot = new Bot { PlayerId = -20 };
        bot.Player.State = PlayerState.EXPLORE_1;
        bot.Player.Interactions.Begin(213, 0);
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            match.RegisterPlayer(bot.Player);
            SendMovements(match, [new BotMovementResult
            {
                BotPlayerId = -20, FromArea = AreaType.S2Gym1, ToArea = AreaType.S2Gym1,
                Position = TestMapPosition.In(AreaType.S2Gym1), Velocity = new Vector3f(1, 0, 0),
                ToCell = network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1)
            }]);
            Assert.Null(bot.Player.Interactions.PendingInteractId);
            Assert.Equal(PlayerState.IDLE, bot.Player.State);
        }
        Assert.Equal(new[] { (1L, Protocol.G_TO_C_PLAYER_INFO), (1L, Protocol.G_TO_C_MOVE) }, timeline);
        Assert.Equal(PlayerState.IDLE, Assert.Single(recipient.Read<G_TO_C_PLAYER_INFO>(Protocol.G_TO_C_PLAYER_INFO).Players).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovementIsSentToItsAreaInProtocolOrder(bool changesArea)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(44001);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var previous = AddRecipient(store, match.MatchingId, 1, AreaType.S2Corridor9, timeline);
        var destination = AddRecipient(store, match.MatchingId, 2, AreaType.S2Gym1, timeline);
        var elsewhere = AddRecipient(store, match.MatchingId, 3, AreaType.S2Library1, timeline);
        var otherMatch = AddRecipient(store, 44002, 4, AreaType.S2Gym1, timeline);
        var movement = new BotMovementResult
        {
            BotPlayerId = -20,
            FromArea = changesArea ? AreaType.S2Corridor9 : AreaType.S2Gym1,
            ToArea = AreaType.S2Gym1,
            ToCell = network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1),
            Position = TestMapPosition.In(AreaType.S2Gym1),
            Velocity = new Vector3f(3, 4, 0),
            Rotation = 17,
            IsAreaTransition = changesArea
        };
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(movement.ToArea)));
        bot.Player.Cell = movement.ToCell;
        bot.Player.Position = movement.Position;
        bot.Player.State = PlayerState.SLEEP;
        using (match.Enter())
        {
            match.Bots.GetBots().Add(bot);
            match.RegisterPlayer(bot.Player);
            float phase = bot.Player.Orbs.OrbitPhaseDegrees;
            SendMovements(match, [movement]);
            Assert.Equal(phase, bot.Player.Orbs.OrbitPhaseDegrees);
        }

        if (changesArea)
        {
            Assert.Equal(
                new[] { (1L, Protocol.G_TO_C_OBJECT_LEAVE), (2L, Protocol.G_TO_C_OBJECT_ENTER), (2L, Protocol.G_TO_C_MOVE) },
                timeline);
            var appearance = Assert.Single(destination.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Players);
            Assert.Equal(PlayerState.SLEEP, appearance.State);
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
        Assert.Equal(TestMapPosition.In(AreaType.S2Gym1).X, sent.Position.X);
        Assert.Equal(network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Gym1).Y, sent.Cell.Y);
        Assert.Equal(3f, sent.Velocity.X);
        Assert.Equal(17f, sent.Rotation);
    }

    [Fact]
    public void ExternalMovementRequiresLockAndRejectsEndedMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(44003);
        var service = new MatchSynchronizationService();
        var batch = new MatchSynchronizationService.SyncBatch(DateTime.UtcNow, []);
        Assert.Throws<InvalidOperationException>(() => service.CollectMovementUpdates(match, new GameObjectInfo(), batch));
        using (match.Enter())
        {
            match.TryMarkEnded();
            Assert.Throws<InvalidOperationException>(() => service.CollectMovementUpdates(match, new GameObjectInfo(), batch));
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
            MonsterId = 1, Alive = true, Health = 10,
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
        Assert.DoesNotContain(recipient.Packets, packet => packet.Protocol == Protocol.G_TO_C_MONSTER_INFO);
        foreach (var session in runtime.GetSessions())
            session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.True(Assert.Single(recipient.Read<G_TO_C_OBJECT_ENTER>(Protocol.G_TO_C_OBJECT_ENTER).Monsters).IsAlive);
        recipient.Packets.Clear();

        runtime.Monsters.Initialize(now);
        var combat = new game_server.matches.monsters.MonsterCombatService();
        combat.ApplyMonsterDamage(runtime, monster.CombatTargetId, 101, 5, now);
        Assert.Empty(recipient.Packets);
        new MatchSynchronizationService().ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Equal(5, Assert.Single(recipient.Read<G_TO_C_MONSTER_INFO>(Protocol.G_TO_C_MONSTER_INFO).Monsters).CurrentHealth);
        Assert.All(elsewhere.Packets, p => Assert.Equal(Protocol.G_TO_C_MOVE, p.Protocol));
        Assert.Empty(otherMatch.Packets);

        recipient.Packets.Clear();
        runtime.RemoveMonster(monster);
        Assert.False(Assert.Single(recipient.Read<G_TO_C_MONSTER_INFO>(Protocol.G_TO_C_MONSTER_INFO).Monsters).IsAlive);
        Assert.Empty(runtime.Monsters.Entities);
    }
    [Fact]
    public void BotAndMonsterSharePacketAndUnchangedMonsterMetadataIsNotResent()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44100);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44100, 1, AreaType.S2Gym1, timeline);
        using var scope = runtime.Enter();
        var bot = new Bot { PlayerId = -20 };
        bot.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)));
        runtime.Bots.GetBots().Add(bot);
        runtime.RegisterPlayer(bot.Player);
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[1] = monster;
        var objects = new List<(GameObjectInfo Info, AreaType FromArea)>
        {
            (bot.Player.GameInfo.ObjectInfo, AreaType.S2Gym1),
            (monster.Info.ObjectInfo, AreaType.S2Gym1)
        };
        CompleteMovements(runtime, objects);
        var packet = recipient.Read<G_TO_C_MOVE>(Protocol.G_TO_C_MOVE);
        Assert.Equal(2, packet.Objects.Count);
        Assert.Equal(ObjectType.PLAYER, packet.Objects[0].ObjectType);
        Assert.Equal(ObjectType.MONSTER, packet.Objects[1].ObjectType);
        Assert.Equal(bot.Player.Orbs.OrbitPhaseDegrees, packet.OrbPhases[-20]);
        Assert.True(packet.ServerTimestamp > 0);
        Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, recipient.Packets[0].Protocol);
        recipient.Packets.Clear();
        CompleteMovements(runtime, objects);
        Assert.Single(recipient.Packets);
        Assert.Equal(Protocol.G_TO_C_MOVE, recipient.Packets[0].Protocol);
        monster.Health = 4;
        recipient.Packets.Clear();
        CompleteMovements(runtime, objects);
        Assert.Equal(4, Assert.Single(recipient.Read<G_TO_C_MONSTER_INFO>(Protocol.G_TO_C_MONSTER_INFO).Monsters).CurrentHealth);
    }

    [Fact]
    public void MonsterDepartureReachesOldAreaAndReentryRestoresAppearance()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44101);
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var oldArea = AddRecipient(store, 44101, 1, AreaType.S2Gym1, timeline);
        var newArea = AddRecipient(store, 44101, 2, AreaType.S2Library1, timeline);
        using var scope = runtime.Enter();
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 1, Alive = true, Health = 10, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1)))
        };
        runtime.Monsters.Entities[1] = monster;
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Gym1)]);
        oldArea.Packets.Clear();
        newArea.Packets.Clear();
        monster.Info.ObjectInfo.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1));
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Gym1)]);
        var leave = Assert.Single(oldArea.Read<G_TO_C_OBJECT_LEAVE>(Protocol.G_TO_C_OBJECT_LEAVE).Objects);
        Assert.Equal(ObjectType.MONSTER, leave.Type);
        Assert.Equal(1, leave.Id);
        Assert.DoesNotContain(oldArea.Packets, p => p.Protocol == Protocol.G_TO_C_MOVE);
        Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, newArea.Packets[0].Protocol);
        oldArea.Packets.Clear();
        monster.Info.ObjectInfo.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1));
        CompleteMovements(runtime, [(monster.Info.ObjectInfo, AreaType.S2Library1)]);
        Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, oldArea.Packets[0].Protocol);
    }

    [Fact]
    public void HumanMovementPacketUsesSameObjectListAndPreservesCorrectionData()
    {
        var info = new GameObjectInfo
        {
            ObjectType = ObjectType.PLAYER, ObjectId = 42, MapId = Config.SWARM_MATCH_MAP,
             Cell = new Cell(3, 4), Position = new Vector3f(10, 20, 0),
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
        Assert.Equal(network.common.data.GameMapData.GetCurrentArea(sent.MapId, sent.Cell), sent.Area);
        Assert.Equal(123456, message.ServerTimestamp);
        Assert.Equal(45f, message.OrbPhases[42]);
    }

    [Fact]
    public void CountdownSupplyPublishesNewMonstersWithoutMovementTick()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(44200);
        var spawnArea = SwarmPressureField.GetKnownAreas().First(area =>
            SwarmPressureField.GetAreaCellsByDistance(area).Any(entry => entry.Distance == SwarmPressureField.MaxDistance));
        var timeline = new List<(long PlayerId, Protocol Protocol)>();
        var recipient = AddRecipient(store, 44200, 101, spawnArea, timeline);
        var otherMatch = AddRecipient(store, 44201, 102, spawnArea, timeline);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.Monsters.Initialize(now);
        runtime.Monsters.Rng = new Random(42);
        var supply = new game_server.matches.monsters.MatchMonsterSpawnService();
        supply.ProcessTick(runtime, now);
        Assert.NotEmpty(runtime.Monsters.Entities);
        Assert.All(runtime.Monsters.Entities.Values, monster =>
        {
            Assert.Empty(monster.Movement.Waypoints);
        });
        Assert.NotEmpty(recipient.Packets);
        Assert.All(recipient.Packets, p => Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, p.Protocol));
        Assert.Empty(otherMatch.Packets);
        recipient.Packets.Clear();
        new MatchMoveService(null!, null!).ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(recipient.Packets);
        supply.ProcessTick(runtime, now.AddMilliseconds(50));
        Assert.Empty(recipient.Packets);

        // 생성 후 늦게 입장한 세션은 기존 상태 전체를 한 번 받는다.
        var late = AddRecipient(store, 44200, 103, spawnArea, timeline);
        var session = runtime.GetSessions().Single(s => s.PlayerId == 103);
        session.SendMonsterSnapshot(runtime.Monsters.GetVisualStatesByArea());
        Assert.NotEmpty(late.Packets);
        Assert.All(late.Packets, p => Assert.Equal(Protocol.G_TO_C_OBJECT_ENTER, p.Protocol));
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
        session.Player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(area)));
        session.PublishedInteractionArea = area;
        TestGameSessionServices.AttachSession(session);
        foreach (var other in store.GetOrCreate(matchingId).GetSessions())
        {
            if (other.Player.GameInfo.ObjectInfo.Area != area || other == session) continue;
            session.PublishedObjects.Add((ObjectType.PLAYER, other.Player.PlayerId));
            other.PublishedObjects.Add((ObjectType.PLAYER, playerId));
        }
        return connection;
    }

    private sealed class RecordingConnection(long playerId, List<(long PlayerId, Protocol Protocol)> timeline) : TcpConnection
    {
        public bool AcceptPackets { get; set; } = true;
        public List<(Protocol Protocol, byte[] Wire)> Packets { get; } = [];

        public override bool TrySend(Packet packet)
        {
            if (!AcceptPackets) return false;
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
