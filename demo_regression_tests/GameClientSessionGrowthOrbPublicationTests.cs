using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.contracts.authentication;
using network.contracts.scaling;
using network.core;
using network.helpers;
using network.hosting;
using network.infrastructure;
using network.packets;
using network.utils;

namespace demo_regression_tests;

public sealed class GameClientSessionGrowthOrbPublicationTests
{
    private const long FirstMatchingId = 71001;
    private const long SecondMatchingId = 71002;
    private const long FirstPlayerId = 101;
    private const long SecondPlayerId = 202;

    public GameClientSessionGrowthOrbPublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public async Task GrowthPick_StaleRejectAndMultiply_PreserveExactStateAndWire()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        SwarmMatchRuntime runtime = fixture.Runtime(FirstMatchingId);
        var offer = new SwarmGrowthOfferState(
            OfferId: runtime.GrowthOfferCoordinator.AllocateOfferId(),
            Cost: 3,
            SpawnItemId: 107000010,
            EnhanceTargetTier: 0,
            ArmorCount: 1,
            CostSummon: 3,
            CostAttack: 4,
            CostDefense: 5);
        runtime.GrowthOfferCoordinator.RegisterOffer(FirstPlayerId, offer);
        fixture.SummonStones.AddStones(FirstMatchingId, FirstPlayerId, 20);

        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK
            {
                OfferId = offer.OfferId + 100,
                CardIndex = SwarmGrowthOfferState.CardMultiply
            });

        Assert.Equal([Protocol.G_TO_C_SWARM_GROWTH_RESULT], token.DeliveredProtocols);
        G_TO_C_SWARM_GROWTH_RESULT stale = token.DeserializeSingle<G_TO_C_SWARM_GROWTH_RESULT>(
            Protocol.G_TO_C_SWARM_GROWTH_RESULT);
        Assert.Equal(offer.OfferId + 100, stale.OfferId);
        Assert.Equal(SwarmGrowthOfferState.CardMultiply, stale.CardIndex);
        Assert.False(stale.Success);
        Assert.Equal(20, stale.StoneCount);
        Assert.Equal(offer, runtime.GrowthOffers.Offers[(FirstMatchingId, FirstPlayerId)]);
        Assert.Empty(fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));

        token.ClearPackets();
        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK
            {
                OfferId = offer.OfferId,
                CardIndex = SwarmGrowthOfferState.CardEnhance
            });

        Assert.Equal(
            [Protocol.G_TO_C_SWARM_GROWTH_RESULT, Protocol.G_TO_C_SUMMON_STONE_STATE],
            token.DeliveredProtocols);
        G_TO_C_SWARM_GROWTH_RESULT rejected = token.DeserializeSingle<G_TO_C_SWARM_GROWTH_RESULT>(
            Protocol.G_TO_C_SWARM_GROWTH_RESULT);
        Assert.Equal(offer.OfferId, rejected.OfferId);
        Assert.Equal(SwarmGrowthOfferState.CardEnhance, rejected.CardIndex);
        Assert.False(rejected.Success);
        Assert.Equal(20, rejected.StoneCount);
        Assert.Equal(offer, runtime.GrowthOffers.Offers[(FirstMatchingId, FirstPlayerId)]);
        Assert.Equal(0, fixture.SummonStones.GetGrowthSuccessCount(FirstMatchingId, FirstPlayerId));

        token.ClearPackets();
        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK
            {
                OfferId = offer.OfferId,
                CardIndex = SwarmGrowthOfferState.CardMultiply
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_SWARM_FAMILY_LEVELS,
                Protocol.G_TO_C_SWARM_GROWTH_RESULT,
                Protocol.G_TO_C_SUMMON_STONE_STATE
            ],
            token.DeliveredProtocols);
        InGameItemInfo added = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(offer.SpawnItemId, added.ItemId);
        Assert.Equal(17, fixture.SummonStones.GetSnapshot(FirstMatchingId, FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.SummonStones.GetGrowthSuccessCount(FirstMatchingId, FirstPlayerId));
        Assert.Equal(
            1,
            fixture.SummonStones.GetGrowthSuccessCount(
                FirstMatchingId,
                FirstPlayerId,
                SwarmGrowthOfferState.CardMultiply));
        Assert.DoesNotContain((FirstMatchingId, FirstPlayerId), runtime.GrowthOffers.Offers.Keys);

        G_TO_C_SWARM_GROWTH_RESULT applied = token.DeserializeSingle<G_TO_C_SWARM_GROWTH_RESULT>(
            Protocol.G_TO_C_SWARM_GROWTH_RESULT);
        Assert.Equal(offer.OfferId, applied.OfferId);
        Assert.Equal(SwarmGrowthOfferState.CardMultiply, applied.CardIndex);
        Assert.True(applied.Success);
        Assert.Equal(17, applied.StoneCount);
        G_TO_C_SUMMON_STONE_STATE stoneState = token.DeserializeSingle<G_TO_C_SUMMON_STONE_STATE>(
            Protocol.G_TO_C_SUMMON_STONE_STATE);
        Assert.Equal(17, stoneState.State.StoneCount);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task OrbDecision_InvalidAndSuccess_PreserveExactStateAndWire()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        Assert.True(fixture.Inventories.TryAddItemWithCapacity(
            FirstMatchingId,
            FirstPlayerId,
            107000010,
            Config.SWARM_ORB_CAPACITY,
            out InGameItemInfo? original));
        Assert.NotNull(original);
        fixture.SummonStones.AddStones(FirstMatchingId, FirstPlayerId, 20);

        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION
            {
                Action = 999,
                TargetItemUid = (long)OrbColor.Red,
                SecondItemUid = 123
            });

        Assert.Equal([Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT], token.DeliveredProtocols);
        G_TO_C_SWARM_ORB_DECISION_RESULT invalid =
            token.DeserializeSingle<G_TO_C_SWARM_ORB_DECISION_RESULT>(
                Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT);
        Assert.Equal(999, invalid.Action);
        Assert.False(invalid.Success);
        Assert.Equal(0, invalid.ResultItemId);
        Assert.Equal((long)OrbColor.Red, invalid.TargetItemUid);
        Assert.Equal(20, invalid.StoneCount);
        Assert.Equal(-1, invalid.TargetOrdinal);
        Assert.Equal(107000010, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId)).ItemId);
        Assert.Equal(0, fixture.Runtime(FirstMatchingId).OrbBoard.GetFamilyUpgradeCount(
            FirstPlayerId,
            OrbColor.Red));

        token.ClearPackets();
        int upgradeCost = Math.Min(
            Config.SWARM_GROWTH_COST_CAP,
            Config.GetSwarmGrowthBaseCost(0));
        Assert.True(OrbData.TryGetItemId(OrbColor.Red, 2, out int upgradedItemId));
        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION
            {
                Action = Config.SWARM_ORB_DECISION_FAMILY_UPGRADE,
                TargetItemUid = (long)OrbColor.Red,
                SecondItemUid = 0
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_SWARM_FAMILY_LEVELS,
                Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT
            ],
            token.DeliveredProtocols);
        InGameItemInfo upgraded = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(original!.ItemUid, upgraded.ItemUid);
        Assert.Equal(upgradedItemId, upgraded.ItemId);
        Assert.Equal(
            20 - upgradeCost,
            fixture.SummonStones.GetSnapshot(FirstMatchingId, FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.Runtime(FirstMatchingId).OrbBoard.GetFamilyUpgradeCount(
            FirstPlayerId,
            OrbColor.Red));

        G_TO_C_SWARM_ORB_DECISION_RESULT success =
            token.DeserializeSingle<G_TO_C_SWARM_ORB_DECISION_RESULT>(
                Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT);
        Assert.Equal(Config.SWARM_ORB_DECISION_FAMILY_UPGRADE, success.Action);
        Assert.True(success.Success);
        Assert.Equal(upgradedItemId, success.ResultItemId);
        Assert.Equal((long)OrbColor.Red, success.TargetItemUid);
        Assert.Equal(20 - upgradeCost, success.StoneCount);
        Assert.Equal(0, success.TargetOrdinal);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizingAfterOuterLease_RejectsBeforeCleanupWithoutCallingHandler(bool orbDecision)
    {
        using var fixture = new SessionFixture();
        var timeline = new ConcurrentQueue<string>();
        int growthCalls = 0;
        int orbCalls = 0;
        GameClientSession session = fixture.CreateSession(
            FirstMatchingId,
            FirstPlayerId,
            growthHandler: (_, _, _, _) => growthCalls++,
            orbHandler: (_, _, _, _, _) => orbCalls++);
        RecordingUserToken token = fixture.TokenFor(session);
        token.BeforeSend = protocol => timeline.Enqueue($"send:{protocol}");
        fixture.AfterMessageLeaseAcquired = matchingId =>
        {
            Assert.True(fixture.Registry.TryFinalize(
                matchingId,
                static () => true,
                () =>
                {
                    timeline.Enqueue("cleanup");
                    fixture.Coordinator.ClearMatching(matchingId);
                }));
        };

        if (orbDecision)
        {
            await SendAsync(
                session,
                Protocol.C_TO_G_SWARM_ORB_DECISION,
                new C_TO_G_SWARM_ORB_DECISION
                {
                    Action = Config.SWARM_ORB_DECISION_FAMILY_UPGRADE,
                    TargetItemUid = (long)OrbColor.Red
                });
        }
        else
        {
            await SendAsync(
                session,
                Protocol.C_TO_G_SWARM_GROWTH_PICK,
                new C_TO_G_SWARM_GROWTH_PICK { OfferId = 17, CardIndex = 2 });
        }

        Protocol expected = orbDecision
            ? Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT
            : Protocol.G_TO_C_SWARM_GROWTH_RESULT;
        Assert.Equal([$"send:{expected}", "cleanup"], timeline);
        Assert.Equal([expected], token.DeliveredProtocols);
        Assert.Equal(0, growthCalls);
        Assert.Equal(0, orbCalls);
        if (orbDecision)
        {
            G_TO_C_SWARM_ORB_DECISION_RESULT result =
                token.DeserializeSingle<G_TO_C_SWARM_ORB_DECISION_RESULT>(expected);
            Assert.False(result.Success);
            Assert.Equal(-1, result.TargetOrdinal);
        }
        else
        {
            G_TO_C_SWARM_GROWTH_RESULT result =
                token.DeserializeSingle<G_TO_C_SWARM_GROWTH_RESULT>(expected);
            Assert.False(result.Success);
            Assert.Equal(17, result.OfferId);
            Assert.Equal(2, result.CardIndex);
        }
        Assert.Null(fixture.Coordinator.Inspect(FirstMatchingId));
    }

    [Fact]
    public async Task GrowthTransportFailure_CommitsAuthoritativeStateAndStopsWireSuffix()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        SwarmMatchRuntime runtime = fixture.Runtime(FirstMatchingId);
        var offer = new SwarmGrowthOfferState(
            OfferId: runtime.GrowthOfferCoordinator.AllocateOfferId(),
            Cost: 3,
            SpawnItemId: 107000010,
            EnhanceTargetTier: 0,
            ArmorCount: 1,
            CostSummon: 3,
            CostAttack: 4,
            CostDefense: 5);
        runtime.GrowthOfferCoordinator.RegisterOffer(FirstPlayerId, offer);
        fixture.SummonStones.AddStones(FirstMatchingId, FirstPlayerId, 20);
        token.ThrowOnceOn = Protocol.G_TO_C_SWARM_FAMILY_LEVELS;

        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK
            {
                OfferId = offer.OfferId,
                CardIndex = SwarmGrowthOfferState.CardMultiply
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_SWARM_FAMILY_LEVELS,
                Protocol.G_TO_C_ERROR
            ],
            token.AttemptedProtocols);
        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_ERROR
            ],
            token.DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_SWARM_GROWTH_RESULT, token.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_SUMMON_STONE_STATE, token.AttemptedProtocols);
        Assert.Equal(offer.SpawnItemId, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId)).ItemId);
        Assert.Equal(17, fixture.SummonStones.GetSnapshot(FirstMatchingId, FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.SummonStones.GetGrowthSuccessCount(FirstMatchingId, FirstPlayerId));
        Assert.DoesNotContain((FirstMatchingId, FirstPlayerId), runtime.GrowthOffers.Offers.Keys);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task OrbTransportFailure_CommitsUpgradeStopsWireSuffixAndReleasesRuntime()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        Assert.True(fixture.Inventories.TryAddItemWithCapacity(
            FirstMatchingId,
            FirstPlayerId,
            107000010,
            Config.SWARM_ORB_CAPACITY,
            out InGameItemInfo? original));
        Assert.NotNull(original);
        fixture.SummonStones.AddStones(FirstMatchingId, FirstPlayerId, 20);
        int upgradeCost = Math.Min(
            Config.SWARM_GROWTH_COST_CAP,
            Config.GetSwarmGrowthBaseCost(0));
        Assert.True(OrbData.TryGetItemId(OrbColor.Red, 2, out int upgradedItemId));
        token.ThrowOnceOn = Protocol.G_TO_C_SWARM_FAMILY_LEVELS;

        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION
            {
                Action = Config.SWARM_ORB_DECISION_FAMILY_UPGRADE,
                TargetItemUid = (long)OrbColor.Red
            });

        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_SWARM_FAMILY_LEVELS,
                Protocol.G_TO_C_ERROR
            ],
            token.AttemptedProtocols);
        Assert.Equal(
            [
                Protocol.G_TO_C_INGAME_INVENTORY_LIST,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT,
                Protocol.G_TO_C_ERROR
            ],
            token.DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT, token.AttemptedProtocols);
        InGameItemInfo upgraded = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(original!.ItemUid, upgraded.ItemUid);
        Assert.Equal(upgradedItemId, upgraded.ItemId);
        Assert.Equal(
            20 - upgradeCost,
            fixture.SummonStones.GetSnapshot(FirstMatchingId, FirstPlayerId).StoneCount);
        Assert.Equal(1, fixture.Runtime(FirstMatchingId).OrbBoard.GetFamilyUpgradeCount(
            FirstPlayerId,
            OrbColor.Red));
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);

        bool cleanupRan = false;
        Assert.True(fixture.Registry.TryFinalize(
            FirstMatchingId,
            static () => true,
            () =>
            {
                cleanupRan = true;
                fixture.Coordinator.ClearMatching(FirstMatchingId);
            }));
        Assert.True(cleanupRan);
        Assert.Null(fixture.Coordinator.Inspect(FirstMatchingId));
    }

    [Fact]
    public async Task PreparationFailure_DispatchesCapturedPrefixAndStopsPreparationSuffix()
    {
        using var fixture = new SessionFixture();
        bool prefixCommitted = false;
        bool suffixCommitted = false;
        GameClientSession session = fixture.CreateSession(
            FirstMatchingId,
            FirstPlayerId,
            growthHandler: (current, _, offerId, cardIndex) =>
            {
                prefixCommitted = true;
                current.SendSwarmGrowthResult(offerId, cardIndex, success: false);
                throw new InvalidOperationException("growth preparation failed");
#pragma warning disable CS0162
                suffixCommitted = true;
#pragma warning restore CS0162
            });

        await SendAsync(
            session,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 23, CardIndex = 2 });

        Assert.True(prefixCommitted);
        Assert.False(suffixCommitted);
        Assert.Equal(
            [Protocol.G_TO_C_SWARM_GROWTH_RESULT, Protocol.G_TO_C_ERROR],
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task DifferentMatches_DispatchIndependentlyWhileFirstGrowthTransportIsBlocked()
    {
        using var fixture = new SessionFixture();
        GameClientSession first = fixture.CreateSession(
            FirstMatchingId,
            FirstPlayerId,
            growthHandler: static (session, _, offerId, cardIndex) =>
                session.SendSwarmGrowthResult(offerId, cardIndex, success: false));
        GameClientSession second = fixture.CreateSession(
            SecondMatchingId,
            SecondPlayerId,
            growthHandler: static (session, _, offerId, cardIndex) =>
                session.SendSwarmGrowthResult(offerId, cardIndex, success: false));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        fixture.TokenFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_SWARM_GROWTH_RESULT)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => SendAsync(
            first,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 1, CardIndex = 0 }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task secondTask = SendAsync(
            second,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 2, CardIndex = 1 });
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            [Protocol.G_TO_C_SWARM_GROWTH_RESULT],
            fixture.TokenFor(second).DeliveredProtocols);
        Assert.False(firstTask.IsCompleted);

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
        Assert.False(fixture.Coordinator.Inspect(SecondMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task InjectedHandlers_AreSessionLocal_AndIdentitySemanticsStayUnchanged()
    {
        using var fixture = new SessionFixture();
        int firstGrowthCalls = 0;
        int secondGrowthCalls = 0;
        int firstOrbCalls = 0;
        int secondOrbCalls = 0;
        GameClientSession first = fixture.CreateSession(
            FirstMatchingId,
            FirstPlayerId,
            growthHandler: (_, _, _, _) => firstGrowthCalls++,
            orbHandler: (_, _, _, _, _) => firstOrbCalls++);
        GameClientSession second = fixture.CreateSession(
            SecondMatchingId,
            SecondPlayerId,
            growthHandler: (_, _, _, _) => secondGrowthCalls++,
            orbHandler: (_, _, _, _, _) => secondOrbCalls++);

        await SendAsync(
            first,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 1, CardIndex = 0 });
        await SendAsync(
            second,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION { Action = 999 });

        Assert.Equal(1, firstGrowthCalls);
        Assert.Equal(0, secondGrowthCalls);
        Assert.Equal(0, firstOrbCalls);
        Assert.Equal(1, secondOrbCalls);

        fixture.SetPlayerId(first, null);
        fixture.SetMatchingId(second, 0);
        await SendAsync(
            first,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 2, CardIndex = 0 });
        await SendAsync(
            second,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION { Action = 999 });
        Assert.Equal(1, firstGrowthCalls);
        Assert.Equal(1, secondOrbCalls);

        fixture.SetPlayerId(first, FirstPlayerId);
        fixture.SetMatchingId(second, SecondMatchingId);
        fixture.SetPlayerMatchStatus(first, PlayerMatchStatus.ELIMINATED);
        fixture.SetPlayerMatchStatus(second, PlayerMatchStatus.ELIMINATED);
        await SendAsync(
            first,
            Protocol.C_TO_G_SWARM_GROWTH_PICK,
            new C_TO_G_SWARM_GROWTH_PICK { OfferId = 3, CardIndex = 0 });
        await SendAsync(
            second,
            Protocol.C_TO_G_SWARM_ORB_DECISION,
            new C_TO_G_SWARM_ORB_DECISION { Action = 999 });

        Assert.Equal(2, firstGrowthCalls);
        Assert.Equal(1, secondOrbCalls);
        Assert.Empty(fixture.TokenFor(first).DeliveredProtocols);
        Assert.Empty(fixture.TokenFor(second).DeliveredProtocols);
        Assert.Null(typeof(GameClientSession).GetProperty(
            "SwarmGrowthPickCallback",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(GameClientSession).GetProperty(
            "SwarmOrbDecisionCallback",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void SourceScope_ActivatesOnlyHumanGrowthAndOrbOuterHandlers()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.cs");
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.OrbSummon.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "GameServer.SwarmOrbBoard.cs");

        Assert.Contains("_handleSwarmGrowthPick", session);
        Assert.Contains("_handleSwarmOrbDecision", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", session);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", arena);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", arena);
        Assert.Equal(2, CountOccurrences(orbSummon, "PublishOrderedSessionAction("));
        Assert.Contains("CurrentMapSubId <= 0", ReadMethodSlice(
            orbSummon,
            "private Task HandleSwarmGrowthPick(",
            "private Task HandleSwarmOrbDecision("));
        Assert.Contains("IsEliminated", ReadMethodSlice(
            orbSummon,
            "private Task HandleSwarmOrbDecision(",
            "internal void SendSwarmFamilyLevels("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            orbSummon,
            "private Task HandleSummonOrb(",
            "internal bool ExecuteDraftOrbSummon("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            orbSummon,
            "private Task HandleDestroyOrb(",
            "internal void SendSummonStoneState("));
        Assert.DoesNotContain("BeginOrderedTurn", arena);
        Assert.DoesNotContain("BeginOrderedTurn", orbBoard);
        Assert.DoesNotContain("PublishOrderedSessionAction", arena);
        Assert.DoesNotContain("PublishOrderedSessionAction", orbBoard);
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

        await session.OnMessageFromClient(new Const<byte[]>(wireBytes));
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
        private readonly Dictionary<GameClientSession, RecordingUserToken> _tokens = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-growth-orb-publication-tests",
            Guid.NewGuid().ToString("N"));
        private readonly Action<GameClientSession, long, int, int> _growthHandler;
        private readonly Action<GameClientSession, long, int, long, long> _orbHandler;

        public SessionFixture()
        {
            Server = CreateServer();
            Registry = GetField<MatchRuntimeRegistry>(Server, "_matchRuntimeRegistry");
            Coordinator = GetField<SwarmCombatPublicationCoordinator>(
                Server,
                "_swarmCombatPublicationCoordinator");
            Inventories = GetField<InGameInventoryManager>(Server, "_inGameInventoryManager");
            SummonStones = GetField<SummonStoneManager>(Server, "_summonStoneManager");
            EventLog = GetField<GameEventLogManager>(Server, "_gameEventLogManager");
            Runtimes = GetField<SwarmMatchRuntimeStore>(Server, "_swarmMatchRuntimes");
            Registry.SetRuntimeInitializer(matchingId =>
            {
                if (!Coordinator.RegisterMatching(matchingId))
                    throw new InvalidOperationException($"Duplicate publication runtime {matchingId}.");
            });
            PublishOrdered = typeof(GameServer).GetMethod(
                    "PublishOrderedSessionPublication",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<long, Action, Action>>(Server);
            _growthHandler = typeof(GameServer).GetMethod(
                    "HandleSwarmGrowthPick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .CreateDelegate<Action<GameClientSession, long, int, int>>(Server);
            _orbHandler = typeof(GameServer).GetMethod(
                    "HandleSwarmOrbDecision",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .CreateDelegate<Action<GameClientSession, long, int, long, long>>(Server);

            Interactables.Initialize();
            Inventories.Initialize();
        }

        public GameServer Server { get; }
        public MatchRuntimeRegistry Registry { get; }
        public SwarmCombatPublicationCoordinator Coordinator { get; }
        public SwarmMatchRuntimeStore Runtimes { get; }
        public Action<long, Action, Action> PublishOrdered { get; }
        public InGameInventoryManager Inventories { get; }
        public SummonStoneManager SummonStones { get; }
        public GameEventLogManager EventLog { get; }
        public InteractableStateManager Interactables { get; } = new();
        public AreaItemStockManager AreaStocks { get; } = new(false);
        public GroundItemManager GroundItems { get; } = new();
        public DoorStateManager Doors { get; } = new();
        public MatchRosterManager Roster { get; } = new(NullLogger.Instance);
        public AreaClosureManager Closures { get; } = new(NullLogger.Instance);
        public BotPlayerManager Bots { get; } = new(NullLogger.Instance);
        public EncounterRevealManager Encounters { get; } = new();
        public Action<long>? AfterMessageLeaseAcquired { get; set; }

        public GameClientSession CreateSession(
            long matchingId,
            long playerId,
            Action<GameClientSession, long, int, int>? growthHandler = null,
            Action<GameClientSession, long, int, long, long>? orbHandler = null)
        {
            AreaStocks.InitializeMatching(matchingId);
            GroundItems.InitializeMatching(matchingId);
            Doors.InitializeMatching(matchingId);

            var token = new RecordingUserToken();
            Activate(token);
            var session = new GameClientSession(
                token,
                null!,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                (_, instanceId) => _sessions
                    .Where(candidate => candidate.CurrentMapSubId == instanceId)
                    .ToList(),
                Interactables,
                Inventories,
                AreaStocks,
                GroundItems,
                SummonStones,
                Doors,
                Roster,
                Closures,
                Bots,
                EventLog,
                new MatchSummaryFileStore(_summaryDirectory),
                Encounters,
                Coordinator.TryCapturePacket,
                PublishOrdered,
                static (_, publish) => publish(),
                growthHandler ?? _growthHandler,
                orbHandler ?? _orbHandler,
                AcquireOperation,
                Registry.TryExecute,
                Registry.TryBindOwnerFence,
                static (_, _, _) => { },
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                static _ => { });
            SetIdentity(session, matchingId, playerId);
            _sessions.Add(session);
            _tokens.Add(session, token);
            return session;
        }

        public RecordingUserToken TokenFor(GameClientSession session) => _tokens[session];

        public SwarmMatchRuntime Runtime(long matchingId) => Runtimes.GetOrCreate(matchingId);

        public void SetMatchingId(GameClientSession session, long matchingId) =>
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);

        public void SetPlayerId(GameClientSession session, long? playerId) =>
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);

        public void SetPlayerMatchStatus(GameClientSession session, PlayerMatchStatus status) =>
            SetProperty(session, nameof(GameClientSession.PlayerMatchStatus), status);

        public void Dispose()
        {
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private IDisposable? AcquireOperation(long matchingId, Action onAcquired)
        {
            IDisposable? operation = Registry.TryAcquireOperation(matchingId, onAcquired);
            if (operation != null)
                AfterMessageLeaseAcquired?.Invoke(matchingId);
            return operation;
        }

        private static void SetIdentity(GameClientSession session, long matchingId, long playerId)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);
            SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
            SetProperty(session, nameof(GameClientSession.CurrentArea), Config.SWARM_MATCH_GROUND_AREA);
            typeof(GameClientSession).GetField(
                "_lastValidatedPosition",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                session,
                new Vector3f(0f, 0f, 0f));
        }

        private static void SetProperty(GameClientSession session, string name, object? value) =>
            typeof(GameClientSession).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

        private static T GetField<T>(object owner, string name) where T : class =>
            Assert.IsType<T>(owner.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));

        private static void Activate(UserToken token)
        {
            int active = (int)typeof(UserToken).GetField(
                "StateActive",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            typeof(UserToken).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(token, active);
        }

        private static GameServer CreateServer() => new(
            new ConfigurationBuilder().Build(),
            NullLogger<GameServer>.Instance,
            null!,
            null!,
            null!,
            null!,
            new ServerConfig
            {
                ServerType = "GameServer",
                ServerId = 1,
                GameServerNum = 1
            },
            null!,
            new GameServerScalingOptions { Enabled = false },
            null!,
            null!,
            new ServerReadinessState());
    }

    private sealed class RecordingUserToken : UserToken
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

        public override void Send(Packet msg)
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

            using var packet = Packet.Create(new Const<byte[]>(wireBytes));
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
