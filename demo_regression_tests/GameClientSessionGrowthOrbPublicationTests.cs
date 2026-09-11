using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.matches;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.helpers;
using network.hosting;
using network.packets;

namespace demo_regression_tests;

public sealed class GameClientSessionGrowthOrbPublicationTests
{
    [Fact]
    public async Task SummonEvents_AreRecordedBeforeInventorySendFailure()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        var connection = fixture.ConnectionFor(session);
        fixture.Store.GetOrThrow(FirstMatchingId).StartGameplay();
        try
        {
            TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 20);
            connection.ThrowOnceOn = Protocol.G_TO_C_ORB_UPDATE;
            connection.BeforeSend = protocol =>
            {
                if (protocol != Protocol.G_TO_C_ORB_UPDATE) return;
                Assert.Contains(fixture.EventLog.GetRecent(FirstMatchingId),
                    entry => entry.Type == GameEventType.SurvivorOrbBoardState && entry.Outcome == "summon");
            };
            await SendAsync(session, Protocol.C_TO_G_SUMMON_ORB, new C_TO_G_SUMMON_ORB());
            var events = fixture.EventLog.GetRecent(FirstMatchingId);
            Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbBoardState && entry.Outcome == "summon");
            Assert.Single(events, entry => entry.Type == GameEventType.OrbSummonSucceeded);
            Assert.Contains(Protocol.G_TO_C_ORB_UPDATE, connection.AttemptedProtocols);
            Assert.DoesNotContain(Protocol.G_TO_C_ORB_UPDATE, connection.DeliveredProtocols);
        }
        finally
        {

        }
    }

    [Fact]
    public async Task AutomaticSummon_ChargesPublishedCostWithoutDraftOrChoice()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        var connection = fixture.ConnectionFor(session);
        fixture.Store.GetOrThrow(FirstMatchingId).StartGameplay();
        try
        {
            TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 20);
            for (int index = 0; index < 2; index++)
            {
                connection.ClearPackets();
                var before = TestGameSessionServices.SummonStones(runtime, FirstPlayerId);
                session.SendSummonStoneState();
                var state = connection.DeserializeSingle<G_TO_C_SUMMON_STONE_STATE>(Protocol.G_TO_C_SUMMON_STONE_STATE);
                Assert.Equal(before.NextCost, state.State.NextCost);
                await SendAsync(session, Protocol.C_TO_G_SUMMON_ORB, new C_TO_G_SUMMON_ORB());
                var result = connection.DeserializeSingle<G_TO_C_SUMMON_ORB_RESULT>(Protocol.G_TO_C_SUMMON_ORB_RESULT);
                Assert.True(result.Success);
                Assert.Equal(before.StoneCount - state.State.NextCost, result.State.StoneCount);
                Assert.Equal(before.SuccessfulSummonCount + 1, result.State.SuccessfulSummonCount);
                Assert.Equal(index + 1, TestGameSessionServices.Orbs(runtime, FirstPlayerId).GetAllItems().Count);
            }
        }
        finally
        {

        }
    }

    [Fact]
    public async Task AutomaticSummon_RejectsInsufficientCurrencyWithoutGrantingItem()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        fixture.Store.GetOrThrow(FirstMatchingId).StartGameplay();
        try
        {
            await SendAsync(session, Protocol.C_TO_G_SUMMON_ORB, new C_TO_G_SUMMON_ORB());
            var result = fixture.ConnectionFor(session).DeserializeSingle<G_TO_C_SUMMON_ORB_RESULT>(Protocol.G_TO_C_SUMMON_ORB_RESULT);
            Assert.False(result.Success);
            Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, result.ErrorCode);
            Assert.Equal(0, result.State.SuccessfulSummonCount);
            Assert.Empty(TestGameSessionServices.Orbs(runtime, FirstPlayerId).GetAllItems());
        }
        finally
        {

        }
    }

    [Fact]
    public async Task FirstSummonPublishesUpgradeCostAndAllowsFamilyUpgrade()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = session.Match;
        var connection = fixture.ConnectionFor(session);
        fixture.Store.GetOrThrow(FirstMatchingId).StartGameplay();
        try
        {
            using (runtime.Enter())
            {
                TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 100);
                session.SendOrbUpgradeInfo(fixture.Server.GetOrbGrowth().GetOrbUpgradeInfo(fixture.Runtime(FirstMatchingId), session.Player));
            }
            var empty = connection.DeserializeSingle<G_TO_C_ORB_UPGRADE_INFO>(Protocol.G_TO_C_ORB_UPGRADE_INFO);
            Assert.Equal(0, empty.SunCost + empty.WindCost + empty.WaveCost);
            connection.ClearPackets();

            await SendAsync(session, Protocol.C_TO_G_SUMMON_ORB, new C_TO_G_SUMMON_ORB());
            var summon = connection.DeserializeSingle<G_TO_C_SUMMON_ORB_RESULT>(Protocol.G_TO_C_SUMMON_ORB_RESULT);
            Assert.True(summon.Success);
            var costs = connection.DeserializeSingle<G_TO_C_ORB_UPGRADE_INFO>(Protocol.G_TO_C_ORB_UPGRADE_INFO);
            Assert.True(OrbData.TryGetColorAndTier(summon.SummonedItemId, out var color, out int tier));
            int cost = color switch
            {
                OrbColor.Red => costs.SunCost,
                OrbColor.Green => costs.WindCost,
                OrbColor.Blue => costs.WaveCost,
                _ => throw new InvalidOperationException("Unexpected orb color")
            };
            Assert.True(cost > 0);
            int stonesBefore = TestGameSessionServices.SummonStones(runtime, FirstPlayerId).StoneCount;
            connection.ClearPackets();

            await SendAsync(session, Protocol.C_TO_G_UPGRADE_ORB,
                new C_TO_G_UPGRADE_ORB { Action = Config.ORB_UPGRADE_GROUP, TargetItemId = summon.SummonedItemId });
            var upgrade = connection.DeserializeSingle<G_TO_C_UPGRADE_ORB_RESULT>(Protocol.G_TO_C_UPGRADE_ORB_RESULT);
            Assert.True(upgrade.Success);
            Assert.True(OrbData.TryGetColorAndTier(upgrade.ResultItemId, out var upgradedColor, out int upgradedTier));
            Assert.Equal(color, upgradedColor);
            Assert.Equal(tier + 1, upgradedTier);
            Assert.Equal(stonesBefore - cost, upgrade.StoneCount);
        }
        finally
        {

        }
    }
    [Fact]
    public void OrbUpgradeInfoQueryDoesNotSendOrChangePlayerState()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = session.Match;
        using (runtime.Enter())
        {
            TestGameSessionServices.Orbs(runtime, FirstPlayerId).TryAddItemWithCapacity(107000010, Config.SWARM_ORB_CAPACITY, out _);
            TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 20);
            var before = TestGameSessionServices.SummonStones(runtime, FirstPlayerId);
            var info = fixture.Server.GetOrbGrowth().GetOrbUpgradeInfo(fixture.Runtime(FirstMatchingId), session.Player);
            Assert.True(info.SunCost > 0);
            Assert.Equal(0, info.WindCost);
            Assert.Equal(0, info.WaveCost);
            Assert.Equal(before, TestGameSessionServices.SummonStones(runtime, FirstPlayerId));
            Assert.Equal(0, runtime.GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
            Assert.Empty(fixture.ConnectionFor(session).DeliveredProtocols);
        }
    }
    [Fact]
    public void SummonStoneStatePacket_OnlyContainsCurrencyCountAndCost()
    {
        var state = new SummonStoneStateInfo
        {
            StoneCount = 7, SuccessfulSummonCount = 2, NextCost = 6
        };
        var fields = MessagePackSerializer.Deserialize<Dictionary<string, int>>(
            MessagePackSerializer.Serialize(state));
        Assert.Equal(3, fields.Count);
        Assert.Equal(7, fields["stoneCount"]);
        Assert.Equal(2, fields["successfulSummonCount"]);
        Assert.Equal(6, fields["nextCost"]);
    }

    [Fact]
    public void AutomaticSummonRequest_HasNoChoiceIndex()
    {
        Assert.Equal("[]", MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(new C_TO_G_SUMMON_ORB())));
        Assert.NotNull(MessagePackSerializer.Deserialize<C_TO_G_SUMMON_ORB>(new byte[] { 0x90 }));
    }

    [Fact]
    public void MovementWakeAndSnapshotsUseConditionStateWithoutClearingBuffs()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        using (runtime.Enter())
        {
            session.Player.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 2, 1, 10);
            Assert.True(session.Player.TryStartSleep(DateTime.UtcNow));
            Assert.Equal(PlayerState.SLEEP, TestGameSessionServices.GetMovement(session).CreateGameObjectInfo(session.Match, session.Player, session.Player.State).State);

            Assert.True(session.Player.TryStopSleep());
            session.SendPlayerState();

            Assert.Equal(PlayerState.IDLE, session.Player.State);
            Assert.False(session.Player.IsSleeping);
            Assert.True(TestGameSessionServices.GetPeriodicBuffCount(session.Player) > 0);
            Assert.Equal(PlayerState.IDLE, TestGameSessionServices.GetMovement(session).CreateGameObjectInfo(session.Match, session.Player, session.Player.State).State);

            session.Player.State = PlayerState.EXPLORE_1;
            Assert.Equal(PlayerState.EXPLORE_1, TestGameSessionServices.GetMovement(session).CreateGameObjectInfo(session.Match, session.Player, session.Player.State).State);
        }
    }

    [Fact]
    public void CombatActorsUsePlayerStateWithoutRequiringSessionsOrBotManagerEntries()
    {
        using var fixture = new SessionFixture();
        var runtime = fixture.Store.GetOrCreate(FirstMatchingId);
        var cell = GameMapData.GetMapInfo(Config.SWARM_MATCH_MAP)!.GetInitialPosition().Item1;
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var human = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = FirstPlayerId },
            Position = position, CurrentArea = area
        };
        var bot = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = -1 },
            Position = position, CurrentArea = area
        };
        using var scope = runtime.Enter();
        runtime.RegisterParticipant(human);
        runtime.RegisterParticipant(bot);
        foreach (var player in new[] { human, bot })
            Assert.True(TestGameSessionServices.Orbs(runtime, player.PlayerId).TryAddItemWithCapacity(107000010, Config.SWARM_ORB_CAPACITY, out _));
        var combat = fixture.Server.GetCombat(FirstMatchingId);
        var actors = combat.BuildSwarmArenaCombatActors(runtime, runtime.GetAlivePlayers(), DateTime.UtcNow);
        var humanActors = actors.Where(actor => actor.PlayerId == human.PlayerId).ToList();
        var botActors = actors.Where(actor => actor.PlayerId == bot.PlayerId).ToList();
        Assert.NotEmpty(humanActors);
        Assert.Equal(humanActors.Select(actor => (actor.WeaponItemId, actor.Damage)),
            botActors.Select(actor => (actor.WeaponItemId, actor.Damage)));
        Assert.Null(human.Session);
        Assert.Null(bot.Session);

        var snapshot = runtime.GetAlivePlayers();
        runtime.TryEliminatePlayer(bot.PlayerId, EliminationReason.HEALTH_ZERO);
        actors = combat.BuildSwarmArenaCombatActors(runtime, snapshot, DateTime.UtcNow);
        Assert.DoesNotContain(actors, actor => actor.PlayerId == bot.PlayerId);
    }

    [Fact]
    public void SleepingPlayer_KeepsAutomaticAttackActors()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        var spawnCell = GameMapData.GetMapInfo(Config.SWARM_MATCH_MAP)!.GetInitialPosition().Item1;
        var spawnArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, spawnCell);
        TestGameSessionServices.SetMovementProperty(session, "CurrentArea", spawnArea);
        TestGameSessionServices.SetMovementProperty(session, "Position", MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell));
        using var matchLock = runtime.Enter();
        Assert.True(TestGameSessionServices.Orbs(runtime, FirstPlayerId).TryAddItemWithCapacity(107000010, Config.SWARM_ORB_CAPACITY, out _));
        var condition = session.Player;
        var now = DateTime.UtcNow;
        List<ProximityCombatActor> Build() => fixture.Server.GetCombat(FirstMatchingId)
            .BuildSwarmArenaCombatActors(runtime, [session.Player], now);

        var awake = Build();
        Assert.Contains(awake, actor => actor.WeaponItemId != 0 && actor.Damage > 0);
        condition.State = PlayerState.SLEEP;
        var sleeping = Build();

        Assert.Equal(awake, sleeping);
        Assert.True(condition.IsSleeping);
    }

    [Fact]
    public async Task ExtractedRuleRequests_PublishWhileHoldingMatchLock()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var connection = fixture.ConnectionFor(session);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        var locks = new List<bool>();
        connection.BeforeSend = _ => locks.Add(Monitor.IsEntered(runtime.MatchLock));
        await SendAsync(session, Protocol.C_TO_G_SUMMON_ORB, new C_TO_G_SUMMON_ORB());
        await SendAsync(session, Protocol.C_TO_G_DOOR_OPEN_START, new C_TO_G_DOOR_OPEN_START { InteractId = int.MaxValue });
        Assert.Contains(Protocol.G_TO_C_SUMMON_ORB_RESULT, connection.DeliveredProtocols);
        Assert.Contains(Protocol.G_TO_C_DOOR_OPEN_ACK, connection.DeliveredProtocols);
        Assert.NotEmpty(locks);
        Assert.All(locks, held => Assert.True(held));
    }
    private const long FirstMatchingId = 71001;
    private const long SecondMatchingId = 71002;
    private const long FirstPlayerId = 101;
    private const long SecondPlayerId = 202;

    public GameClientSessionGrowthOrbPublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }



    [Theory]
    [InlineData(OrbColor.Red)]
    [InlineData(OrbColor.Green)]
    [InlineData(OrbColor.Blue)]
    public async Task OrbUpgrade_InvalidAndSuccess_PreserveExactStateAndWire(OrbColor color)
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        Assert.True(OrbData.TryGetItemId(color, 1, out int originalItemId));
        int orbGroupId = OrbData.GetOrbGroupId(originalItemId);
        Assert.True(TestGameSessionServices.Orbs(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).TryAddItemWithCapacity(originalItemId,
            Config.SWARM_ORB_CAPACITY,
            out InGameItemInfo? original));
        Assert.NotNull(original);
        TestGameSessionServices.AddSummonStones(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId, 20);

        await SendAsync(
            session,
            Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB
            {
                Action = 999,
                TargetItemId = originalItemId,
            });

        Assert.Equal([Protocol.G_TO_C_UPGRADE_ORB_RESULT], connection.DeliveredProtocols);
        G_TO_C_UPGRADE_ORB_RESULT invalid =
            connection.DeserializeSingle<G_TO_C_UPGRADE_ORB_RESULT>(
                Protocol.G_TO_C_UPGRADE_ORB_RESULT);
        Assert.Equal(999, invalid.Action);
        Assert.False(invalid.Success);
        Assert.Equal(0, invalid.ResultItemId);
        Assert.Equal(originalItemId, invalid.TargetItemId);
        Assert.Equal(20, invalid.StoneCount);
        Assert.Equal(-1, invalid.TargetOrdinal);
        Assert.Equal(originalItemId, Assert.Single(
            TestGameSessionServices.Orbs(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).GetAllItems()).ItemId);
        Assert.Equal(0, fixture.Runtime(FirstMatchingId).GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(orbGroupId));

        connection.ClearPackets();
        int upgradeCost = Math.Min(
            Config.SWARM_GROWTH_COST_CAP,
            Config.GetSwarmGrowthBaseCost(0));
        Assert.True(OrbData.TryGetItemId(color, 2, out int upgradedItemId));
        await SendAsync(
            session,
            Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB
            {
                Action = Config.ORB_UPGRADE_GROUP,
                TargetItemId = originalItemId,
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_ORB_LIST,
                Protocol.G_TO_C_ORB_UPGRADE_INFO,
                Protocol.G_TO_C_UPGRADE_ORB_RESULT
            ],
            connection.DeliveredProtocols);
        InGameItemInfo upgraded = Assert.Single(
            TestGameSessionServices.Orbs(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).GetAllItems());
        Assert.Equal(original!.ItemUid, upgraded.ItemUid);
        Assert.Equal(upgradedItemId, upgraded.ItemId);
        Assert.Equal(
            20 - upgradeCost,
            TestGameSessionServices.SummonStones(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.Runtime(FirstMatchingId).GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(orbGroupId));

        G_TO_C_UPGRADE_ORB_RESULT success =
            connection.DeserializeSingle<G_TO_C_UPGRADE_ORB_RESULT>(
                Protocol.G_TO_C_UPGRADE_ORB_RESULT);
        Assert.Equal(Config.ORB_UPGRADE_GROUP, success.Action);
        Assert.True(success.Success);
        Assert.Equal(upgradedItemId, success.ResultItemId);
        Assert.Equal(originalItemId, success.TargetItemId);
        Assert.Equal(20 - upgradeCost, success.StoneCount);
        Assert.Equal(0, success.TargetOrdinal);
        Assert.False(Monitor.IsEntered(fixture.Store.GetOrNull(FirstMatchingId)!.MatchLock));
    }

    [Fact]
    public async Task TerminalWhileWaitingForLock_RejectsUpgrade()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var connection = fixture.ConnectionFor(session);
        var runtime = fixture.Store.GetOrThrow(FirstMatchingId);
        using var lockHeld = new ManualResetEventSlim();
        using var markTerminal = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using (MatchRuntimeStore.Enter(runtime))
            {
                lockHeld.Set();
                Assert.True(markTerminal.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(runtime.TryMarkEnded());
            }
        });
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));
        Task message = Task.Run(() => SendAsync(session, Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB
            {
                Action = Config.ORB_UPGRADE_GROUP,
                TargetItemId = 107000010
            }));
        await Task.Delay(100);
        Assert.False(message.IsCompleted);
        markTerminal.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await message.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([Protocol.G_TO_C_UPGRADE_ORB_RESULT], connection.DeliveredProtocols);
        var result = connection.DeserializeSingle<G_TO_C_UPGRADE_ORB_RESULT>(
            Protocol.G_TO_C_UPGRADE_ORB_RESULT);
        Assert.False(result.Success);
        Assert.Equal(-1, result.TargetOrdinal);
        Assert.Null(fixture.Store.GetOrNull(FirstMatchingId));
    }



    [Fact]
    public async Task OrbTransportFailure_CommitsUpgradeStopsWireSuffixAndReleasesLock()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        Assert.True(TestGameSessionServices.Orbs(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).TryAddItemWithCapacity(107000010,
            Config.SWARM_ORB_CAPACITY,
            out InGameItemInfo? original));
        Assert.NotNull(original);
        TestGameSessionServices.AddSummonStones(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId, 20);
        int upgradeCost = Math.Min(
            Config.SWARM_GROWTH_COST_CAP,
            Config.GetSwarmGrowthBaseCost(0));
        Assert.True(OrbData.TryGetItemId(OrbColor.Red, 2, out int upgradedItemId));
        connection.ThrowOnceOn = Protocol.G_TO_C_ORB_UPGRADE_INFO;

        await SendAsync(
            session,
            Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB
            {
                Action = Config.ORB_UPGRADE_GROUP,
                TargetItemId = 107000010
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_ORB_LIST,
                Protocol.G_TO_C_ORB_UPGRADE_INFO,
                Protocol.G_TO_C_ERROR
            ],
            connection.AttemptedProtocols);
        Assert.Equal(
            [
                Protocol.G_TO_C_ORB_LIST,
                Protocol.G_TO_C_ERROR
            ],
            connection.DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_UPGRADE_ORB_RESULT, connection.AttemptedProtocols);
        InGameItemInfo upgraded = Assert.Single(
            TestGameSessionServices.Orbs(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).GetAllItems());
        Assert.Equal(original!.ItemUid, upgraded.ItemUid);
        Assert.Equal(upgradedItemId, upgraded.ItemId);
        Assert.Equal(
            20 - upgradeCost,
            TestGameSessionServices.SummonStones(fixture.Store.GetOrThrow(FirstMatchingId), FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.Runtime(FirstMatchingId).GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
        Assert.False(Monitor.IsEntered(fixture.Store.GetOrNull(FirstMatchingId)!.MatchLock));

        // 실패한 핸들러가 잠금을 풀었으므로 종료 정리가 바로 진행된다.
        fixture.MarkTerminal(FirstMatchingId);
        Assert.Null(fixture.Store.GetOrNull(FirstMatchingId));
        Assert.Null(fixture.Store.GetOrNull(FirstMatchingId));
    }

    [Fact]
    public async Task OrbHandler_SendsResultAfterActualUpgradeCommits()
    {
        using var fixture = new SessionFixture();
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var runtime = session.Match;
        Assert.True(TestGameSessionServices.Orbs(runtime, FirstPlayerId).TryAddItemWithCapacity(107000010,
            Config.SWARM_ORB_CAPACITY, out var original));
        TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 20);
        int cost = Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(0));
        Assert.True(OrbData.TryGetItemId(OrbColor.Red, 2, out int upgradedItemId));
        var connection = fixture.ConnectionFor(session);
        connection.BeforeSend = _ =>
        {
            Assert.Equal(upgradedItemId, Assert.Single(TestGameSessionServices.Orbs(runtime, FirstPlayerId).GetAllItems()).ItemId);
            Assert.Equal(20 - cost, TestGameSessionServices.SummonStones(runtime, FirstPlayerId).StoneCount);
            Assert.Equal(1, runtime.GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
        };
        await SendAsync(session, Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB { Action = Config.ORB_UPGRADE_GROUP, TargetItemId = 107000010 });
        var result = connection.DeserializeSingle<G_TO_C_UPGRADE_ORB_RESULT>(Protocol.G_TO_C_UPGRADE_ORB_RESULT);
        Assert.True(result.Success);
        Assert.Equal(upgradedItemId, result.ResultItemId);
        Assert.Equal(0, result.TargetOrdinal);
        Assert.Equal(20 - cost, result.StoneCount);
        Assert.Equal(107000010, result.TargetItemId);
        Assert.Equal(Config.ORB_UPGRADE_GROUP, result.Action);
    }

    [Fact]
    public async Task UpgradeLogFailure_ReportsErrorWithoutSuccessAndReleasesLock()
    {
        using var fixture = new SessionFixture();
        bool logAttempted = false;
        var failingLog = new GameEventLogManager(_ =>
        {
            logAttempted = true;
            throw new InvalidOperationException("event storage unavailable");
        });
        var session = fixture.CreateSession(FirstMatchingId, FirstPlayerId, growthEventLog: failingLog);
        var runtime = session.Match;
        Assert.True(TestGameSessionServices.Orbs(runtime, FirstPlayerId).TryAddItemWithCapacity(107000010,
            Config.SWARM_ORB_CAPACITY, out _));
        TestGameSessionServices.AddSummonStones(runtime, FirstPlayerId, 20);
        await SendAsync(session, Protocol.C_TO_G_UPGRADE_ORB,
            new C_TO_G_UPGRADE_ORB { Action = Config.ORB_UPGRADE_GROUP, TargetItemId = 107000010 });
        Assert.True(logAttempted);
        Assert.Equal([Protocol.G_TO_C_ERROR], fixture.ConnectionFor(session).DeliveredProtocols);
        // 기록 실패 전 적용된 강화는 되돌리지 않는다. 성공 응답은 보내지 않고 잠금은 해제한다.
        Assert.Equal(1, runtime.GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
        Assert.False(Monitor.IsEntered(runtime.MatchLock));
        fixture.MarkTerminal(FirstMatchingId);
        Assert.Null(fixture.Store.GetOrNull(FirstMatchingId));
    }

    [Fact]
    public async Task DifferentMatches_DispatchIndependentlyWhileFirstUpgradeTransportIsBlocked()
    {
        using var fixture = new SessionFixture();
        var first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var second = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.ConnectionFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_UPGRADE_ORB_RESULT) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var request = new C_TO_G_UPGRADE_ORB
        {
            Action = Config.ORB_UPGRADE_GROUP, TargetItemId = 107000010
        };
        Task firstTask = Task.Run(() => SendAsync(first, Protocol.C_TO_G_UPGRADE_ORB, request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await SendAsync(second, Protocol.C_TO_G_UPGRADE_ORB, request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([Protocol.G_TO_C_UPGRADE_ORB_RESULT], fixture.ConnectionFor(second).DeliveredProtocols);
            Assert.False(firstTask.IsCompleted);
        }
        finally
        {
            release.Set();
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SharedGrowthService_KeepsMatchStateSeparateAndRejectsInvalidIdentity()
    {
        using var fixture = new SessionFixture();
        var first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        var second = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        foreach (var session in new[] { first, second })
        {
            Assert.True(TestGameSessionServices.Orbs(session.Match, session.PlayerId!.Value).TryAddItemWithCapacity(107000010, Config.SWARM_ORB_CAPACITY, out _));
            TestGameSessionServices.AddSummonStones(session.Match, session.PlayerId.Value, 20);
        }
        var firstRuntime = first.Match;
        var secondRuntime = second.Match;
        var request = new C_TO_G_UPGRADE_ORB { Action = Config.ORB_UPGRADE_GROUP, TargetItemId = 107000010 };
        await SendAsync(first, Protocol.C_TO_G_UPGRADE_ORB, request);
        Assert.Equal(1, firstRuntime.GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
        Assert.Equal(0, secondRuntime.GetParticipant(SecondPlayerId)!.GetOrbUpgradeCount(10700001));
        await SendAsync(second, Protocol.C_TO_G_UPGRADE_ORB, request);
        int firstPacketCount = fixture.ConnectionFor(first).DeliveredProtocols.Count;
        int secondPacketCount = fixture.ConnectionFor(second).DeliveredProtocols.Count;
        fixture.SetPlayerId(first, null);
        fixture.SetMatchingId(second, 0);
        await SendAsync(first, Protocol.C_TO_G_UPGRADE_ORB, request);
        await SendAsync(second, Protocol.C_TO_G_UPGRADE_ORB, request);
        fixture.SetPlayerId(first, FirstPlayerId);
        fixture.SetMatchingId(second, SecondMatchingId);
        fixture.SetPlayerMatchStatus(first, PlayerMatchStatus.ELIMINATED);
        fixture.SetPlayerMatchStatus(second, PlayerMatchStatus.ELIMINATED);
        await SendAsync(first, Protocol.C_TO_G_UPGRADE_ORB, request);
        await SendAsync(second, Protocol.C_TO_G_UPGRADE_ORB, request);
        Assert.Equal(1, firstRuntime.GetParticipant(FirstPlayerId)!.GetOrbUpgradeCount(10700001));
        Assert.Equal(1, secondRuntime.GetParticipant(SecondPlayerId)!.GetOrbUpgradeCount(10700001));
        Assert.Equal(firstPacketCount, fixture.ConnectionFor(first).DeliveredProtocols.Count);
        Assert.Equal(secondPacketCount, fixture.ConnectionFor(second).DeliveredProtocols.Count);
    }

    [Fact]
    public void SourceScope_UsesMatchLocksAndHasNoGrowthPickHandler()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.cs");
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Sessions",
            "GameClientSession.Orb.cs");
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "Players", "PlayerOrbGrowthService.cs");

        Assert.Contains("PlayerOrbGrowthService _orbGrowth", session);
        Assert.Contains("_orbGrowth.UpgradeOrb(", orbSummon);
        Assert.DoesNotContain("SwarmGrowthPickCallback", session);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", combat);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", combat);
        Assert.DoesNotContain("RunWithMatchLock", orbSummon);
        Assert.Equal(2, CountOccurrences(orbSummon, "using (match.Enter())"));
        Assert.Equal(2, CountOccurrences(orbSummon, "if (match.IsEnded"));
        Assert.DoesNotContain("HandleSwarmGrowthPick", orbSummon);
        Assert.DoesNotContain("C_TO_G_SWARM_GROWTH_PICK", session);
        Assert.Contains("IsEliminated", ReadMethodSlice(
            orbSummon,
            "private Task HandleUpgradeOrb(",
            "internal void SendOrbUpgradeInfo("));
        Assert.Contains("using (match.Enter())", ReadMethodSlice(orbSummon,
            "private Task HandleSummonOrb(",
            "private Task HandleUpgradeOrb("));
        Assert.DoesNotContain("HandleDestroyOrb", orbSummon);
        Assert.DoesNotContain("C_TO_G_DESTROY_ORB", session);
        Assert.DoesNotContain("RunWithMatchLock", combat);
        Assert.DoesNotContain("RunWithMatchLock", orbBoard);
    }

    private static async Task SendAsync<T>(
        GameClientSession session,
        Protocol protocol,
        T message)
    {
        byte[] wireBytes;
        using (var packet = Packet.Create((int)protocol, session.PlayerId ?? 0))
        {
            packet.SetBody(MessagePackSerializer.Serialize(message));
            packet.RecordSize();
            wireBytes = packet.ToBytes();
        }

        await session.OnMessageFromClient(wireBytes);
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] parts) =>
        File.ReadAllText(Path.Combine([repositoryRoot, .. parts]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly List<GameClientSession> _sessions = [];
        private readonly Dictionary<GameClientSession, RecordingTcpConnection> _connections = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-growth-orb-publication-tests",
            Guid.NewGuid().ToString("N"));


        public SessionFixture()
        {
            Server = CreateServer();
            Store = Server.GetMatchRuntimes();
            EventLog = Server.GetEventLogs();


        }

        public GameServer Server { get; }
        public MatchRuntimeStore Store { get; }
        public GameEventLogManager EventLog { get; }


        public GameClientSession CreateSession(
            long matchingId,
            long playerId, GameEventLogManager? growthEventLog = null)
        {
            Store.GetOrCreate(matchingId);
            Store.GetOrNull(matchingId)!.Doors.Initialize();

            var connection = new RecordingTcpConnection();
            Activate(connection);
            var session = new GameClientSession(
                connection,
                NullLogger.Instance,
                null!,
                static _ => false, TestGameSessionServices.CreateMatchCleanupService(),
                static (_, _) => null,

                EventLog,
                growthEventLog == null ? Server.GetOrbGrowth() : TestGameSessionServices.CreatePlayerOrbGrowthService(Store, growthEventLog),
                TestGameSessionServices.CreateMovementService(EventLog),
                new PlayerInteractionService(),

                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(),
                TestGameSessionServices.CreateEntryService(null!, Store, NullLogger.Instance));
            connection.SetSession(session);
            SetIdentity(session, matchingId, playerId);
            _sessions.Add(session);
            TestGameSessionServices.AttachSession(session);
            _connections.Add(session, connection);
            return session;
        }

        public RecordingTcpConnection ConnectionFor(GameClientSession session) => _connections[session];

        public MatchRuntime Runtime(long matchingId) => Store.GetOrCreate(matchingId);

        /// <summary>잠금 안에서 터미널로 표시하고 나온다 — 정리는 깊이 0 탈출에서 바로 돈다.</summary>
        public void MarkTerminal(long matchingId)
        {
            MatchRuntime runtime = Store.GetOrNull(matchingId)!;
            using (MatchRuntimeStore.Enter(runtime))
            {
                Assert.True(runtime.TryMarkEnded());
            }
        }

        public void SetMatchingId(GameClientSession session, long matchingId)
        {
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            TestGameSessionServices.BindMatch(session, matchingId);
        }

        public void SetPlayerId(GameClientSession session, long? playerId) =>
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);

        public void SetPlayerMatchStatus(GameClientSession session, PlayerMatchStatus status) =>
            session.Player.Status = status;

        public void Dispose()
        {
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private static void SetIdentity(GameClientSession session, long matchingId, long playerId)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            TestGameSessionServices.BindMatch(session, matchingId);
            TestGameSessionServices.SetMovementProperty(session, "CurrentArea", Config.SWARM_MATCH_GROUND_AREA);
            TestGameSessionServices.SetMovementProperty(session, "Position", new Vector3f(0f, 0f, 0f));
        }

        private static void SetProperty(GameClientSession session, string name, object? value) =>
            typeof(GameClientSession).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

        private static T GetField<T>(object owner, string name) where T : class =>
            Assert.IsType<T>(owner.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));

        private static void Activate(TcpConnection connection)
        {
            int active = (int)typeof(TcpConnection).GetField(
                "StateActive",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            typeof(TcpConnection).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        }

        private static GameServer CreateServer(MatchRuntimeStore? runtimes = null) => GameServerTestAccess.Create(runtimes);
    }

    private sealed class RecordingTcpConnection : TcpConnection
    {
        private readonly object _gate = new();
        private readonly List<(Protocol Protocol, byte[] WireBytes)> _delivered = [];
        private readonly List<Protocol> _attempted = [];
        private int _thrown;

        public Protocol? ThrowOnceOn { get; set; }
        public Action<Protocol>? BeforeSend { get; set; }

        public IReadOnlyList<Protocol> DeliveredProtocols
        {
            get
            {
                lock (_gate)
                    return _delivered.Select(entry => entry.Protocol).ToList();
            }
        }

        public IReadOnlyList<Protocol> AttemptedProtocols
        {
            get
            {
                lock (_gate)
                    return _attempted.ToList();
            }
        }

        public override bool TrySend(Packet msg)
        {
            msg.RecordSize();
            var protocol = (Protocol)msg.ProtocolId;
            lock (_gate)
                _attempted.Add(protocol);

            BeforeSend?.Invoke(protocol);
            if (ThrowOnceOn == protocol && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException($"transport failed for {protocol}");

            byte[] wireBytes = msg.ToBytes();
            lock (_gate)
                _delivered.Add((protocol, wireBytes));
            return true;
        }

        public T DeserializeSingle<T>(Protocol protocol)
        {
            byte[] wireBytes;
            lock (_gate)
            {
                wireBytes = Assert.Single(
                    _delivered,
                    entry => entry.Protocol == protocol).WireBytes;
            }

            using var packet = Packet.Create(wireBytes);
            Assert.Equal((int)protocol, packet.PopProtocolId());
            _ = packet.PopPlayerId();
            return MessagePackSerializer.Deserialize<T>(packet.PopBody());
        }

        public void ClearPackets()
        {
            lock (_gate)
            {
                _attempted.Clear();
                _delivered.Clear();
            }
            _thrown = 0;
            ThrowOnceOn = null;
            BeforeSend = null;
        }
    }
}
