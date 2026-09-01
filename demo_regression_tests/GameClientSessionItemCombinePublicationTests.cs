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
using network.interfaces;
using network.packets;
using network.utils;

namespace demo_regression_tests;

public sealed class GameClientSessionItemCombinePublicationTests
{
    private const long FirstMatchingId = 72001;
    private const long SecondMatchingId = 72002;
    private const long FirstPlayerId = 101;
    private const long SecondPlayerId = 202;
    private const int RecoveryOrbT1 = 107000040;
    private const int RecoveryOrbT2 = 107000041;
    private const int SunOrbT1 = 107000010;
    private const int SunOrbT2 = 107000011;
    private const int Bandage = 201000008;
    private const int CannedCoffee = 201000011;
    private const int CompressionBandage = 201000019;

    public GameClientSessionItemCombinePublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
        MatchStartGate.RemoveMatching(FirstMatchingId);
        MatchStartGate.RemoveMatching(SecondMatchingId);
    }

    [Fact]
    public async Task RecoveryOrbSuccess_PreservesExactEquippedStateWireAndLogs()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        (InGameItemInfo first, InGameItemInfo second) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Inventories.TryEquipBattleItem(
            FirstMatchingId,
            FirstPlayerId,
            first.ItemUid,
            out _));

        await SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1);

        Assert.Equal(
            [
                Protocol.G_TO_C_ITEMS_COMBINED,
                Protocol.G_TO_C_INGAME_INVENTORY_UPDATE,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT
            ],
            token.DeliveredProtocols);

        G_TO_C_ITEMS_COMBINED combined = token.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(193401, combined.RecipeId);
        Assert.Equal(RecoveryOrbT1, combined.InputItemA);
        Assert.Equal(RecoveryOrbT1, combined.InputItemB);
        Assert.Equal(RecoveryOrbT2, combined.OutputItemId);
        Assert.Equal("각성한 회복 오브", combined.OutputItemName);
        Assert.Equal(0, combined.StaminaReward);
        Assert.False(combined.IsRaceComplete);

        G_TO_C_INGAME_INVENTORY_UPDATE inventoryUpdate =
            token.DeserializeSingle<G_TO_C_INGAME_INVENTORY_UPDATE>(
                Protocol.G_TO_C_INGAME_INVENTORY_UPDATE);
        Assert.Equal(3, inventoryUpdate.Items.Count);
        Assert.Equal(
            [first.ItemUid, second.ItemUid],
            inventoryUpdate.Items
                .Where(item => item.ItemId == RecoveryOrbT1 && item.Count == 0)
                .Select(item => item.ItemUid)
                .OrderBy(uid => uid));
        InGameItemInfo outputUpdate = Assert.Single(
            inventoryUpdate.Items,
            item => item.ItemId == RecoveryOrbT2 && item.Count == 1);

        InGameItemInfo liveOutput = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(outputUpdate.ItemUid, liveOutput.ItemUid);
        Assert.Equal(RecoveryOrbT2, liveOutput.ItemId);
        Assert.Equal(
            liveOutput.ItemUid,
            fixture.Inventories.GetEquippedBattleItem(FirstMatchingId, FirstPlayerId)!.ItemUid);

        G_TO_C_USE_INGAME_ITEM_RESULT equipped =
            token.DeserializeSingle<G_TO_C_USE_INGAME_ITEM_RESULT>(
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT);
        Assert.True(equipped.Success);
        Assert.Equal(liveOutput.ItemUid, equipped.ItemUid);
        Assert.Equal(ErrorCode.SUCCESS, equipped.ErrorCode);

        List<GameEventEntry> events = fixture.EventLog.GetForPersistence(FirstMatchingId);
        Assert.Equal(["MISSION", "SURVIVOR_FIRST_T2"], events.Select(entry => entry.Type));
        Assert.Contains(
            $"{RecoveryOrbT1} + {RecoveryOrbT1} => {RecoveryOrbT2}",
            events[0].Description);
        Assert.Equal(RecoveryOrbT2, events[1].WeaponItemId);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task TrueOrbBranchSuccess_PreservesRecipeZeroBoardLogsAndEquippedOutput()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            SunOrbT1);
        Assert.True(fixture.Inventories.TryEquipBattleItem(
            FirstMatchingId,
            FirstPlayerId,
            first.ItemUid,
            out _));

        await SendCombineAsync(session, SunOrbT1, SunOrbT1);

        Assert.Equal(
            [
                Protocol.G_TO_C_ITEMS_COMBINED,
                Protocol.G_TO_C_INGAME_INVENTORY_UPDATE,
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT
            ],
            token.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED combined = token.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(0, combined.RecipeId);
        Assert.Equal(SunOrbT1, combined.InputItemA);
        Assert.Equal(SunOrbT1, combined.InputItemB);
        Assert.True(OrbData.TryGetColorAndTier(combined.OutputItemId, out OrbColor color, out int tier));
        Assert.NotEqual(OrbColor.None, color);
        Assert.Equal(2, tier);
        int outputItemId = combined.OutputItemId;

        InGameItemInfo output = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(outputItemId, output.ItemId);
        Assert.Equal(
            output.ItemUid,
            fixture.Inventories.GetEquippedBattleItem(FirstMatchingId, FirstPlayerId)!.ItemUid);
        Assert.Equal(
            output.ItemUid,
            token.DeserializeSingle<G_TO_C_USE_INGAME_ITEM_RESULT>(
                Protocol.G_TO_C_USE_INGAME_ITEM_RESULT).ItemUid);

        List<GameEventEntry> events = fixture.EventLog.GetForPersistence(FirstMatchingId);
        Assert.Equal(
            [
                "SURVIVOR_ORB_BOARD_STATE",
                "SURVIVOR_ORB_FIRST_PICKUP",
                "MISSION",
                "SURVIVOR_FIRST_T2"
            ],
            events.Select(entry => entry.Type));
        Assert.Equal("merge", events[0].Outcome);
        Assert.Equal([outputItemId], events[0].BoardItemIds);
        Assert.Equal(outputItemId, events[0].WeaponItemId);
        Assert.Contains("SURVIVOR_ORB_MERGE", events[2].Description);
        Assert.Equal(outputItemId, events[3].WeaponItemId);
    }

    [Fact]
    public async Task OrdinaryRecipeSuccess_PreservesExactTwoPacketResultAndState()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);

        await SendCombineAsync(session, Bandage, Bandage);

        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_INGAME_INVENTORY_UPDATE],
            token.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED result = token.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(193031, result.RecipeId);
        Assert.Equal(Bandage, result.InputItemA);
        Assert.Equal(Bandage, result.InputItemB);
        Assert.Equal(CompressionBandage, result.OutputItemId);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId)).ItemId);
        G_TO_C_INGAME_INVENTORY_UPDATE update =
            token.DeserializeSingle<G_TO_C_INGAME_INVENTORY_UPDATE>(
                Protocol.G_TO_C_INGAME_INVENTORY_UPDATE);
        Assert.Equal(2, update.Items.Count(item => item.ItemId == Bandage && item.Count == 0));
        Assert.Single(update.Items, item => item.ItemId == CompressionBandage && item.Count == 1);
        GameEventEntry mission = Assert.Single(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.Equal("MISSION", mission.Type);
        Assert.Contains($"{Bandage} + {Bandage} => {CompressionBandage}", mission.Description);
    }

    [Theory]
    [InlineData("invalid-orb", ErrorCode.INVALID_PARAMETER)]
    [InlineData("no-recipe", ErrorCode.INSUFFICIENT_ITEM)]
    [InlineData("missing-material", ErrorCode.INSUFFICIENT_ITEM)]
    [InlineData("round-locked", ErrorCode.INVALID_GAME_STATE)]
    public async Task Rejections_PreserveExactTwoPacketFailureAndDoNotMutate(
        string scenario,
        ErrorCode expectedError)
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        int itemA;
        int itemB;

        switch (scenario)
        {
            case "invalid-orb":
                itemA = SunOrbT1;
                itemB = SunOrbT2;
                fixture.Inventories.AddItem(FirstMatchingId, FirstPlayerId, itemA);
                fixture.Inventories.AddItem(FirstMatchingId, FirstPlayerId, itemB);
                break;
            case "no-recipe":
                itemA = Bandage;
                itemB = CannedCoffee;
                fixture.Inventories.AddItem(FirstMatchingId, FirstPlayerId, itemA);
                fixture.Inventories.AddItem(FirstMatchingId, FirstPlayerId, itemB);
                break;
            case "missing-material":
                itemA = Bandage;
                itemB = Bandage;
                fixture.Inventories.AddItem(FirstMatchingId, FirstPlayerId, itemA);
                break;
            case "round-locked":
                itemA = Bandage;
                itemB = Bandage;
                fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
                fixture.SetPlayerMatchStatus(session, PlayerMatchStatus.ELIMINATED);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        (long ItemUid, int ItemId, int Count)[] before = fixture.InventorySnapshot(
            FirstMatchingId,
            FirstPlayerId);

        await SendCombineAsync(session, itemA, itemB);

        AssertCombineFailure(token, itemA, itemB, expectedError);
        Assert.Equal(before, fixture.InventorySnapshot(FirstMatchingId, FirstPlayerId));
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task MissingPlayerIsSilent_AndMissingMatchPreservesLegacyCoreFailure()
    {
        using var fixture = new SessionFixture();
        GameClientSession missingPlayer = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        fixture.SetPlayerId(missingPlayer, null);

        await SendCombineAsync(missingPlayer, Bandage, Bandage);

        Assert.Empty(fixture.TokenFor(missingPlayer).AttemptedProtocols);

        GameClientSession missingMatch = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        fixture.SetMatchingId(missingMatch, 0);
        await SendCombineAsync(missingMatch, Bandage, Bandage);

        AssertCombineFailure(
            fixture.TokenFor(missingMatch),
            Bandage,
            Bandage,
            ErrorCode.INSUFFICIENT_ITEM);
    }

    [Fact]
    public async Task FinalizingAfterOuterLease_RejectsBeforeCleanupWithoutMutation()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
        (long ItemUid, int ItemId, int Count)[] before = fixture.InventorySnapshot(
            FirstMatchingId,
            FirstPlayerId);
        var timeline = new ConcurrentQueue<string>();
        fixture.TokenFor(session).BeforeSend = protocol => timeline.Enqueue($"send:{protocol}");
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

        await SendCombineAsync(session, Bandage, Bandage);

        Assert.Equal(
            ["send:G_TO_C_ITEMS_COMBINED", "send:G_TO_C_ERROR", "cleanup"],
            timeline);
        AssertCombineFailure(
            fixture.TokenFor(session),
            Bandage,
            Bandage,
            ErrorCode.INVALID_GAME_STATE);
        Assert.Equal(before, fixture.InventorySnapshot(FirstMatchingId, FirstPlayerId));
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.Null(fixture.Coordinator.Inspect(FirstMatchingId));
    }

    [Fact]
    public async Task PendingTerminalWaitsForEntireBundleAndTurnRetirement()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Inventories.TryEquipBattleItem(
            FirstMatchingId,
            FirstPlayerId,
            first.ItemUid,
            out _));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        bool? activeTurnAtCleanup = null;
        fixture.TokenFor(session).BeforeSend = protocol =>
        {
            timeline.Enqueue($"send:{protocol}");
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task message = Task.Run(() => SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);

        Assert.True(fixture.Registry.TryFinalize(
            FirstMatchingId,
            static () => true,
            () =>
            {
                activeTurnAtCleanup = fixture.Coordinator.Inspect(FirstMatchingId)?.HasActiveTurn;
                timeline.Enqueue("cleanup");
                fixture.Coordinator.ClearMatching(FirstMatchingId);
            },
            () => timeline.Enqueue("after")));
        Assert.DoesNotContain("cleanup", timeline);

        release.Set();
        await message.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(activeTurnAtCleanup);
        Assert.Equal(
            [
                "send:G_TO_C_ITEMS_COMBINED",
                "send:G_TO_C_INGAME_INVENTORY_UPDATE",
                "send:G_TO_C_USE_INGAME_ITEM_RESULT",
                "cleanup",
                "after"
            ],
            timeline);
        Assert.Null(fixture.Coordinator.Inspect(FirstMatchingId));
    }

    [Theory]
    [InlineData(Protocol.G_TO_C_ITEMS_COMBINED, 0)]
    [InlineData(Protocol.G_TO_C_INGAME_INVENTORY_UPDATE, 1)]
    [InlineData(Protocol.G_TO_C_USE_INGAME_ITEM_RESULT, 2)]
    public async Task TransportFailure_CommitsStateAndLogsButStopsWireSuffix(
        Protocol failingProtocol,
        int failingIndex)
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingUserToken token = fixture.TokenFor(session);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Inventories.TryEquipBattleItem(
            FirstMatchingId,
            FirstPlayerId,
            first.ItemUid,
            out _));
        token.ThrowOnceOn = failingProtocol;
        Protocol[] bundle =
        [
            Protocol.G_TO_C_ITEMS_COMBINED,
            Protocol.G_TO_C_INGAME_INVENTORY_UPDATE,
            Protocol.G_TO_C_USE_INGAME_ITEM_RESULT
        ];

        await SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1);

        Assert.Equal(
            bundle.Take(failingIndex + 1).Append(Protocol.G_TO_C_ERROR),
            token.AttemptedProtocols);
        Assert.Equal(
            bundle.Take(failingIndex).Append(Protocol.G_TO_C_ERROR),
            token.DeliveredProtocols);
        InGameItemInfo output = Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId));
        Assert.Equal(RecoveryOrbT2, output.ItemId);
        Assert.Equal(
            output.ItemUid,
            fixture.Inventories.GetEquippedBattleItem(FirstMatchingId, FirstPlayerId)!.ItemUid);
        Assert.Equal(
            ["MISSION", "SURVIVOR_FIRST_T2"],
            fixture.EventLog.GetForPersistence(FirstMatchingId).Select(entry => entry.Type));
        G_TO_C_ERROR error = token.DeserializeSingle<G_TO_C_ERROR>(Protocol.G_TO_C_ERROR);
        Assert.Equal(ErrorCode.SERVER_INTERNAL_ERROR, error.ErrorCode);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task CaptureFailure_ReplaysCapturedPrefixAndStopsPreparationSuffix()
    {
        using var fixture = new SessionFixture { ThrowOnCaptureOrdinal = 2 };
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Inventories.TryEquipBattleItem(
            FirstMatchingId,
            FirstPlayerId,
            first.ItemUid,
            out _));

        await SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1);

        RecordingUserToken token = fixture.TokenFor(session);
        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_ERROR],
            token.DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_INGAME_INVENTORY_UPDATE, token.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_USE_INGAME_ITEM_RESULT, token.AttemptedProtocols);
        Assert.Equal(RecoveryOrbT2, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId)).ItemId);
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.Equal(
            ErrorCode.SERVER_INTERNAL_ERROR,
            token.DeserializeSingle<G_TO_C_ERROR>(Protocol.G_TO_C_ERROR).ErrorCode);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task SameMatch_TwoPlayerBundlesAreFifoAndNeverInterleave()
    {
        using var fixture = new SessionFixture();
        GameClientSession first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        GameClientSession second = fixture.CreateSession(FirstMatchingId, SecondPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
        fixture.SeedPair(FirstMatchingId, SecondPlayerId, Bandage);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        fixture.TokenFor(first).BeforeSend = protocol =>
        {
            timeline.Enqueue($"first:{protocol}");
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        fixture.TokenFor(second).BeforeSend = protocol => timeline.Enqueue($"second:{protocol}");

        Task firstTask = Task.Run(() => SendCombineAsync(first, Bandage, Bandage));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task secondTask = Task.Run(() => SendCombineAsync(second, Bandage, Bandage));
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Coordinator.Inspect(FirstMatchingId)?.OrderedWaiterCount == 1,
            TimeSpan.FromSeconds(5)));
        Assert.Empty(fixture.TokenFor(second).AttemptedProtocols);
        Assert.Equal(2, fixture.Inventories.GetAllItems(FirstMatchingId, SecondPlayerId).Count);

        release.Set();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "first:G_TO_C_ITEMS_COMBINED",
                "first:G_TO_C_INGAME_INVENTORY_UPDATE",
                "second:G_TO_C_ITEMS_COMBINED",
                "second:G_TO_C_INGAME_INVENTORY_UPDATE"
            ],
            timeline);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, FirstPlayerId)).ItemId);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Inventories.GetAllItems(FirstMatchingId, SecondPlayerId)).ItemId);
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task DifferentMatches_DispatchIndependentlyWhileFirstBundleIsBlocked()
    {
        using var fixture = new SessionFixture();
        GameClientSession first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        GameClientSession second = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
        fixture.SeedPair(SecondMatchingId, SecondPlayerId, Bandage);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        fixture.TokenFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => SendCombineAsync(first, Bandage, Bandage));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task secondTask = SendCombineAsync(second, Bandage, Bandage);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_INGAME_INVENTORY_UPDATE],
            fixture.TokenFor(second).DeliveredProtocols);
        Assert.False(firstTask.IsCompleted);

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Coordinator.Inspect(FirstMatchingId)!.Value.HasActiveTurn);
        Assert.False(fixture.Coordinator.Inspect(SecondMatchingId)!.Value.HasActiveTurn);
    }

    [Fact]
    public void SourceScope_ActivatesOnlyCombineOuterHandler()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.cs");
        string combine = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.ItemCombine.cs");
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.OrbSummon.cs");
        string playerState = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.PlayerState.cs");

        Assert.Contains(
            "Protocol.C_TO_G_COMBINE_ITEMS,\n            async bytes => await HandleMessage<C_TO_G_COMBINE_ITEMS>(bytes, HandleCombineItems)",
            session);
        Assert.Equal(1, CountOccurrences(combine, "PublishOrderedSessionAction("));
        Assert.Contains("private Task HandleCombineItemsCore(", combine);
        Assert.Contains("CurrentMapSubId <= 0", ReadMethodSlice(
            combine,
            "private Task HandleCombineItems(",
            "private Task HandleCombineItemsCore("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            combine,
            "private Task HandleCombineItemsCore(",
            "private bool TryHandleBattleItemCombine("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            orbSummon,
            "private Task HandleSummonOrb(",
            "internal bool ExecuteDraftOrbSummon("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            orbSummon,
            "private Task HandleDestroyOrb(",
            "private void SendDestroyOrbResult("));
        Assert.DoesNotContain("PublishOrderedSessionAction", ReadMethodSlice(
            playerState,
            "private async Task HandleUseInGameItem(",
            "private bool ApplyItemBuffs("));
    }

    private static async Task SendCombineAsync(
        GameClientSession session,
        int itemA,
        int itemB) =>
        await SendAsync(
            session,
            Protocol.C_TO_G_COMBINE_ITEMS,
            new C_TO_G_COMBINE_ITEMS { ItemA = itemA, ItemB = itemB });

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

    private static void AssertCombineFailure(
        RecordingUserToken token,
        int itemA,
        int itemB,
        ErrorCode errorCode)
    {
        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_ERROR],
            token.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED result = token.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(0, result.RecipeId);
        Assert.Equal(itemA, result.InputItemA);
        Assert.Equal(itemB, result.InputItemB);
        Assert.Equal(0, result.OutputItemId);
        Assert.Equal(string.Empty, result.OutputItemName);
        Assert.Equal(0, result.StaminaReward);
        Assert.False(result.IsRaceComplete);
        G_TO_C_ERROR error = token.DeserializeSingle<G_TO_C_ERROR>(Protocol.G_TO_C_ERROR);
        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal("부품 결합 실패", error.Message);
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
            "orbtail-item-combine-publication-tests",
            Guid.NewGuid().ToString("N"));
        private int _captureOrdinal;

        public SessionFixture()
        {
            Server = CreateServer();
            Registry = GetField<MatchRuntimeRegistry>(Server, "_matchRuntimeRegistry");
            Coordinator = GetField<SwarmCombatPublicationCoordinator>(
                Server,
                "_swarmCombatPublicationCoordinator");
            Inventories = GetField<InGameInventoryManager>(Server, "_inGameInventoryManager");
            EventLog = GetField<GameEventLogManager>(Server, "_gameEventLogManager");
            Registry.SetRuntimeInitializer(matchingId =>
            {
                if (!Coordinator.RegisterMatching(matchingId))
                    throw new InvalidOperationException($"Duplicate publication runtime {matchingId}.");
            });
            PublishOrdered = typeof(GameServer).GetMethod(
                    "PublishOrderedSessionPublication",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<long, Action, Action>>(Server);

            Interactables.Initialize();
            Inventories.Initialize();
        }

        public GameServer Server { get; }
        public MatchRuntimeRegistry Registry { get; }
        public SwarmCombatPublicationCoordinator Coordinator { get; }
        public Action<long, Action, Action> PublishOrdered { get; }
        public InGameInventoryManager Inventories { get; }
        public GameEventLogManager EventLog { get; }
        public InteractableStateManager Interactables { get; } = new();
        public AreaItemStockManager AreaStocks { get; } = new(false);
        public GroundItemManager GroundItems { get; } = new();
        public SummonStoneManager SummonStones { get; } = new();
        public DoorStateManager Doors { get; } = new();
        public MatchRosterManager Roster { get; } = new(NullLogger.Instance);
        public AreaClosureManager Closures { get; } = new(NullLogger.Instance);
        public BotPlayerManager Bots { get; } = new(NullLogger.Instance);
        public EncounterRevealManager Encounters { get; } = new();
        public Action<long>? AfterMessageLeaseAcquired { get; set; }
        public int? ThrowOnCaptureOrdinal { get; init; }

        public GameClientSession CreateSession(long matchingId, long playerId)
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
                TryCapturePacket,
                PublishOrdered,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
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

        public (InGameItemInfo First, InGameItemInfo Second) SeedPair(
            long matchingId,
            long playerId,
            int itemId)
        {
            InGameItemInfo first = Inventories.AddItem(matchingId, playerId, itemId);
            InGameItemInfo second = Inventories.AddItem(matchingId, playerId, itemId);
            Assert.NotEqual(first.ItemUid, second.ItemUid);
            return (first, second);
        }

        public (long ItemUid, int ItemId, int Count)[] InventorySnapshot(
            long matchingId,
            long playerId) =>
            Inventories.GetAllItems(matchingId, playerId)
                .OrderBy(item => item.ItemUid)
                .Select(item => (item.ItemUid, item.ItemId, item.Count))
                .ToArray();

        public void SetMatchingId(GameClientSession session, long matchingId) =>
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);

        public void SetPlayerId(GameClientSession session, long? playerId) =>
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);

        public void SetPlayerMatchStatus(GameClientSession session, PlayerMatchStatus status) =>
            SetProperty(session, nameof(GameClientSession.PlayerMatchStatus), status);

        public void Dispose()
        {
            MatchStartGate.RemoveMatching(FirstMatchingId);
            MatchStartGate.RemoveMatching(SecondMatchingId);
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private bool TryCapturePacket(Action<IPacket> sendDirect, IPacket packet)
        {
            int ordinal = Interlocked.Increment(ref _captureOrdinal);
            if (ThrowOnCaptureOrdinal == ordinal)
                throw new InvalidOperationException($"capture failed at ordinal {ordinal}");
            return Coordinator.TryCapturePacket(sendDirect, packet);
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
    }
}
