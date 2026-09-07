using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.helpers;
using network.hosting;
using network.packets;

namespace demo_regression_tests;

public sealed class GameClientSessionItemCombinePublicationTests
{
    private const long FirstMatchingId = 72001;
    private const long SecondMatchingId = 72002;
    private const long FirstPlayerId = 101;
    private const long SecondPlayerId = 202;
    private const long ThirdPlayerId = 303;
    private const int RecoveryOrbT1 = 107000040;
    private const int RecoveryOrbT2 = 107000041;
    private const int SunOrbT1 = 107000010;
    private const int SunOrbT2 = 107000011;
    private const int WindOrbT2 = 107000021;
    private const int WaveOrbT2 = 107000031;
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
    public void LegacyPayloadKeyAndInt64Shape_RemainCompatible()
    {
        var legacyRequest = new LegacyCombineRequest
        {
            ItemA = Bandage,
            ItemB = Bandage,
            ClientStartUnixMs = long.MaxValue
        };

        C_TO_G_COMBINE_ITEMS currentRequest =
            MessagePackSerializer.Deserialize<C_TO_G_COMBINE_ITEMS>(
                MessagePackSerializer.Serialize(legacyRequest));

        Assert.Equal(Bandage, currentRequest.ItemA);
        Assert.Equal(Bandage, currentRequest.ItemB);
        Assert.Equal(long.MaxValue, currentRequest.ClientStartUnixMs);

        LegacyCombineRequest legacyRoundTrip =
            MessagePackSerializer.Deserialize<LegacyCombineRequest>(
                MessagePackSerializer.Serialize(new C_TO_G_COMBINE_ITEMS
                {
                    ItemA = RecoveryOrbT1,
                    ItemB = RecoveryOrbT2,
                    ClientStartUnixMs = long.MinValue
                }));

        Assert.Equal(RecoveryOrbT1, legacyRoundTrip.ItemA);
        Assert.Equal(RecoveryOrbT2, legacyRoundTrip.ItemB);
        Assert.Equal(long.MinValue, legacyRoundTrip.ClientStartUnixMs);
    }

    [Fact]
    public async Task RecoveryOrbSuccess_PreservesExactEquippedStateWireAndLogs()
    {
        using var fixture = new SessionFixture();
        CountingRandom itemCombineRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 0);
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        (InGameItemInfo first, InGameItemInfo second) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Store.GetRequired(FirstMatchingId).Inventory.TryEquipBattleItem(
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
            connection.DeliveredProtocols);

        G_TO_C_ITEMS_COMBINED combined = connection.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(193401, combined.RecipeId);
        Assert.Equal(RecoveryOrbT1, combined.InputItemA);
        Assert.Equal(RecoveryOrbT1, combined.InputItemB);
        Assert.Equal(RecoveryOrbT2, combined.OutputItemId);
        Assert.Equal("각성한 회복 오브", combined.OutputItemName);
        Assert.Equal(0, combined.StaminaReward);
        Assert.False(combined.IsRaceComplete);

        G_TO_C_INGAME_INVENTORY_UPDATE inventoryUpdate =
            connection.DeserializeSingle<G_TO_C_INGAME_INVENTORY_UPDATE>(
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
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(FirstPlayerId));
        Assert.Equal(outputUpdate.ItemUid, liveOutput.ItemUid);
        Assert.Equal(RecoveryOrbT2, liveOutput.ItemId);
        Assert.Equal(
            liveOutput.ItemUid,
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetEquippedBattleItem(FirstPlayerId)!.ItemUid);

        G_TO_C_USE_INGAME_ITEM_RESULT equipped =
            connection.DeserializeSingle<G_TO_C_USE_INGAME_ITEM_RESULT>(
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
        Assert.False(Monitor.IsEntered(fixture.Store.Get(FirstMatchingId)!.Sync));
        Assert.Equal(1, itemCombineRandom.DrawCount);
        Assert.Equal([0], itemCombineRandom.DrawResults);
        Assert.Equal(1, fixture.ItemCombineDrawCount(FirstMatchingId));
    }

    [Fact]
    public async Task TrueOrbBranchSuccess_PreservesRecipeZeroBoardLogsAndEquippedOutput()
    {
        using var fixture = new SessionFixture();
        CountingRandom itemCombineRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 1);
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            SunOrbT1);
        Assert.True(fixture.Store.GetRequired(FirstMatchingId).Inventory.TryEquipBattleItem(
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
            connection.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED combined = connection.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(0, combined.RecipeId);
        Assert.Equal(SunOrbT1, combined.InputItemA);
        Assert.Equal(SunOrbT1, combined.InputItemB);
        Assert.Equal(WindOrbT2, combined.OutputItemId);
        Assert.True(OrbData.TryGetColorAndTier(combined.OutputItemId, out OrbColor color, out int tier));
        Assert.NotEqual(OrbColor.None, color);
        Assert.Equal(2, tier);
        int outputItemId = combined.OutputItemId;

        InGameItemInfo output = Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(FirstPlayerId));
        Assert.Equal(outputItemId, output.ItemId);
        Assert.Equal(
            output.ItemUid,
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetEquippedBattleItem(FirstPlayerId)!.ItemUid);
        Assert.Equal(
            output.ItemUid,
            connection.DeserializeSingle<G_TO_C_USE_INGAME_ITEM_RESULT>(
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
        Assert.Equal(1, itemCombineRandom.DrawCount);
        Assert.Equal([1], itemCombineRandom.DrawResults);
        Assert.Equal(1, fixture.ItemCombineDrawCount(FirstMatchingId));
    }

    [Fact]
    public async Task OrdinaryRecipeSuccess_PreservesExactTwoPacketResultAndState()
    {
        using var fixture = new SessionFixture();
        CountingRandom itemCombineRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 0);
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);

        await SendCombineAsync(session, Bandage, Bandage);

        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_INGAME_INVENTORY_UPDATE],
            connection.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED result = connection.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(193031, result.RecipeId);
        Assert.Equal(Bandage, result.InputItemA);
        Assert.Equal(Bandage, result.InputItemB);
        Assert.Equal(CompressionBandage, result.OutputItemId);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(FirstPlayerId)).ItemId);
        G_TO_C_INGAME_INVENTORY_UPDATE update =
            connection.DeserializeSingle<G_TO_C_INGAME_INVENTORY_UPDATE>(
                Protocol.G_TO_C_INGAME_INVENTORY_UPDATE);
        Assert.Equal(2, update.Items.Count(item => item.ItemId == Bandage && item.Count == 0));
        Assert.Single(update.Items, item => item.ItemId == CompressionBandage && item.Count == 1);
        GameEventEntry mission = Assert.Single(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.Equal("MISSION", mission.Type);
        Assert.Contains($"{Bandage} + {Bandage} => {CompressionBandage}", mission.Description);
        Assert.Equal(1, itemCombineRandom.DrawCount);
        Assert.Equal([0], itemCombineRandom.DrawResults);
        Assert.Equal(1, fixture.ItemCombineDrawCount(FirstMatchingId));
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
        CountingRandom itemCombineRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 0);
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        int itemA;
        int itemB;

        switch (scenario)
        {
            case "invalid-orb":
                itemA = SunOrbT1;
                itemB = SunOrbT2;
                fixture.Store.GetRequired(FirstMatchingId).Inventory.AddItem(FirstPlayerId, itemA);
                fixture.Store.GetRequired(FirstMatchingId).Inventory.AddItem(FirstPlayerId, itemB);
                break;
            case "no-recipe":
                itemA = Bandage;
                itemB = CannedCoffee;
                fixture.Store.GetRequired(FirstMatchingId).Inventory.AddItem(FirstPlayerId, itemA);
                fixture.Store.GetRequired(FirstMatchingId).Inventory.AddItem(FirstPlayerId, itemB);
                break;
            case "missing-material":
                itemA = Bandage;
                itemB = Bandage;
                fixture.Store.GetRequired(FirstMatchingId).Inventory.AddItem(FirstPlayerId, itemA);
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

        AssertCombineFailure(connection, itemA, itemB, expectedError);
        Assert.Equal(before, fixture.InventorySnapshot(FirstMatchingId, FirstPlayerId));
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.False(Monitor.IsEntered(fixture.Store.Get(FirstMatchingId)!.Sync));
        Assert.Equal(0, itemCombineRandom.DrawCount);
    }

    [Fact]
    public async Task MissingPlayerIsSilent_AndMissingMatchRejectsWithInvalidGameState()
    {
        // #325: matchingId 없는 legacy direct core는 삭제됐다 — Finalizing과 같은
        // 입력 보존 INVALID_GAME_STATE bundle을 lane 밖에서 보내고 난수·재고를 건드리지 않는다.
        using var fixture = new SessionFixture();
        GameClientSession missingPlayer = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        fixture.SetPlayerId(missingPlayer, null);

        await SendCombineAsync(missingPlayer, Bandage, Bandage);

        Assert.Empty(fixture.ConnectionFor(missingPlayer).AttemptedProtocols);

        GameClientSession missingMatch = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        fixture.SeedPair(SecondMatchingId, SecondPlayerId, Bandage);
        (long ItemUid, int ItemId, int Count)[] before = fixture.InventorySnapshot(
            SecondMatchingId,
            SecondPlayerId);
        fixture.SetMatchingId(missingMatch, 0);
        await SendCombineAsync(missingMatch, Bandage, Bandage);

        AssertCombineFailure(
            fixture.ConnectionFor(missingMatch),
            Bandage,
            Bandage,
            ErrorCode.INVALID_GAME_STATE);
        Assert.Equal(before, fixture.InventorySnapshot(SecondMatchingId, SecondPlayerId));
        Assert.Empty(fixture.EventLog.GetForPersistence(SecondMatchingId));
        Assert.Equal(0, fixture.TotalItemCombineDraws);
    }

    [Fact]
    public async Task TerminalWhileWaitingForLock_RejectsWithoutMutation()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
        (long ItemUid, int ItemId, int Count)[] before = fixture.InventorySnapshot(
            FirstMatchingId,
            FirstPlayerId);
        var timeline = new ConcurrentQueue<string>();
        fixture.ConnectionFor(session).BeforeSend = protocol => timeline.Enqueue($"send:{protocol}");
        MatchRuntime runtime = fixture.Store.Get(FirstMatchingId)!;
        var inventoryBeforeRemoval = runtime.Inventory.GetPlayerInventory(FirstPlayerId);
        using var lockHeld = new ManualResetEventSlim();
        using var markTerminal = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using (fixture.Store.Enter(runtime))
            {
                lockHeld.Set();
                Assert.True(markTerminal.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(runtime.TryMarkTerminal());
            }
        });
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));

        Task message = Task.Run(() => SendCombineAsync(session, Bandage, Bandage));
        await Task.Delay(100);
        Assert.False(message.IsCompleted);
        markTerminal.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await message.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["send:G_TO_C_ITEMS_COMBINED", "send:G_TO_C_ERROR"],
            timeline);
        AssertCombineFailure(
            fixture.ConnectionFor(session),
            Bandage,
            Bandage,
            ErrorCode.INVALID_GAME_STATE);
        Assert.Equal(before, inventoryBeforeRemoval.GetAllItems().OrderBy(item => item.ItemUid).Select(item => (item.ItemUid, item.ItemId, item.Count)).ToArray());
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        Assert.Null(fixture.Store.Get(FirstMatchingId));
        Assert.Equal(0, fixture.TotalItemCombineDraws);
    }

    [Fact]
    public async Task PendingTerminalWaitsForEntireBundleAndLockRelease()
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Store.GetRequired(FirstMatchingId).Inventory.TryEquipBattleItem(
            FirstPlayerId,
            first.ItemUid,
            out _));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        fixture.ConnectionFor(session).BeforeSend = protocol =>
        {
            timeline.Enqueue($"send:{protocol}");
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task message = Task.Run(() => SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(fixture.Store.TryEnter(FirstMatchingId, out _));

        fixture.CleanupTimeline = timeline;
        Task terminal = Task.Run(() =>
        {
            fixture.MarkTerminal(FirstMatchingId);
            timeline.Enqueue("after");
        });
        await Task.Delay(100);
        Assert.False(terminal.IsCompleted);
        Assert.DoesNotContain("cleanup", timeline);

        release.Set();
        await message.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "send:G_TO_C_ITEMS_COMBINED",
                "send:G_TO_C_INGAME_INVENTORY_UPDATE",
                "send:G_TO_C_USE_INGAME_ITEM_RESULT",
                "cleanup",
                "after"
            ],
            timeline);
        Assert.Null(fixture.Store.Get(FirstMatchingId));
    }

    [Theory]
    [InlineData(Protocol.G_TO_C_ITEMS_COMBINED, 0)]
    [InlineData(Protocol.G_TO_C_INGAME_INVENTORY_UPDATE, 1)]
    [InlineData(Protocol.G_TO_C_USE_INGAME_ITEM_RESULT, 2)]
    public async Task TransportFailure_CommitsStateButStopsWireAndLogSuffix(
        Protocol failingProtocol,
        int failingIndex)
    {
        using var fixture = new SessionFixture();
        GameClientSession session = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        (InGameItemInfo first, _) = fixture.SeedPair(
            FirstMatchingId,
            FirstPlayerId,
            RecoveryOrbT1);
        Assert.True(fixture.Store.GetRequired(FirstMatchingId).Inventory.TryEquipBattleItem(
            FirstPlayerId,
            first.ItemUid,
            out _));
        connection.ThrowOnceOn = failingProtocol;
        Protocol[] bundle =
        [
            Protocol.G_TO_C_ITEMS_COMBINED,
            Protocol.G_TO_C_INGAME_INVENTORY_UPDATE,
            Protocol.G_TO_C_USE_INGAME_ITEM_RESULT
        ];

        await SendCombineAsync(session, RecoveryOrbT1, RecoveryOrbT1);

        Assert.Equal(
            bundle.Take(failingIndex + 1).Append(Protocol.G_TO_C_ERROR),
            connection.AttemptedProtocols);
        Assert.Equal(
            bundle.Take(failingIndex).Append(Protocol.G_TO_C_ERROR),
            connection.DeliveredProtocols);
        InGameItemInfo output = Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(FirstPlayerId));
        Assert.Equal(RecoveryOrbT2, output.ItemId);
        Assert.Equal(
            output.ItemUid,
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetEquippedBattleItem(FirstPlayerId)!.ItemUid);
        // 송신이 잠금 안에서 바로 나가므로 실패한 Send 뒤의 로그 단계는 돌지 않는다 — 인벤토리 변경은 남는다.
        Assert.Empty(fixture.EventLog.GetForPersistence(FirstMatchingId));
        G_TO_C_ERROR error = connection.DeserializeSingle<G_TO_C_ERROR>(Protocol.G_TO_C_ERROR);
        Assert.Equal(ErrorCode.SERVER_INTERNAL_ERROR, error.ErrorCode);
        Assert.False(Monitor.IsEntered(fixture.Store.Get(FirstMatchingId)!.Sync));
    }

    [Fact]
    public async Task SameMatch_LockIgnoresClientTimestampsAndBundlesNeverInterleave()
    {
        using var fixture = new SessionFixture();
        GameClientSession blocker = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        GameClientSession firstWaiter = fixture.CreateSession(FirstMatchingId, SecondPlayerId);
        GameClientSession secondWaiter = fixture.CreateSession(FirstMatchingId, ThirdPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, Bandage);
        fixture.SeedPair(FirstMatchingId, SecondPlayerId, Bandage);
        fixture.SeedPair(FirstMatchingId, ThirdPlayerId, Bandage);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        fixture.ConnectionFor(blocker).BeforeSend = protocol =>
        {
            timeline.Enqueue($"blocker:{protocol}");
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        fixture.ConnectionFor(firstWaiter).BeforeSend =
            protocol => timeline.Enqueue($"first-waiter:{protocol}");
        fixture.ConnectionFor(secondWaiter).BeforeSend =
            protocol => timeline.Enqueue($"second-waiter:{protocol}");

        Task blockerTask = Task.Run(() => SendCombineAsync(blocker, Bandage, Bandage));
        Task firstWaiterTask = Task.CompletedTask;
        Task secondWaiterTask = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            firstWaiterTask = Task.Run(() => SendCombineAsync(
                firstWaiter,
                Bandage,
                Bandage,
                clientStartUnixMs: long.MaxValue));
            secondWaiterTask = Task.Run(() => SendCombineAsync(
                secondWaiter,
                Bandage,
                Bandage,
                clientStartUnixMs: long.MinValue));
            await Task.Delay(100);
            Assert.False(firstWaiterTask.IsCompleted);
            Assert.False(secondWaiterTask.IsCompleted);
            Assert.Empty(fixture.ConnectionFor(firstWaiter).AttemptedProtocols);
            Assert.Empty(fixture.ConnectionFor(secondWaiter).AttemptedProtocols);
            Assert.Equal(2, fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(SecondPlayerId).Count);
            Assert.Equal(2, fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(ThirdPlayerId).Count);
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(blockerTask, firstWaiterTask, secondWaiterTask)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // 잠금은 대기 순서를 약속하지 않는다 — 번들이 통째로 이어지는 것만 약속한다.
        string[] entries = timeline.ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal("blocker:G_TO_C_ITEMS_COMBINED", entries[0]);
        Assert.Equal("blocker:G_TO_C_INGAME_INVENTORY_UPDATE", entries[1]);
        for (int index = 2; index < entries.Length; index += 2)
        {
            string owner = entries[index][..entries[index].IndexOf(':')];
            Assert.Equal($"{owner}:G_TO_C_ITEMS_COMBINED", entries[index]);
            Assert.Equal($"{owner}:G_TO_C_INGAME_INVENTORY_UPDATE", entries[index + 1]);
        }
        Assert.Equal(
            ["first-waiter", "second-waiter"],
            entries.Skip(2).Select(entry => entry[..entry.IndexOf(':')]).Distinct().Order());
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(FirstPlayerId)).ItemId);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(SecondPlayerId)).ItemId);
        Assert.Equal(CompressionBandage, Assert.Single(
            fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(ThirdPlayerId)).ItemId);
    }

    [Fact]
    public async Task SameMatch_InvertedClientTimestampsDoNotChangeColoredOrbDrawOrder()
    {
        using var fixture = new SessionFixture();
        CountingRandom itemCombineRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 0, 1);
        GameClientSession first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        GameClientSession second = fixture.CreateSession(FirstMatchingId, SecondPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, SunOrbT1);
        fixture.SeedPair(FirstMatchingId, SecondPlayerId, SunOrbT1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.ConnectionFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => SendCombineAsync(
            first,
            SunOrbT1,
            SunOrbT1,
            clientStartUnixMs: long.MaxValue));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, itemCombineRandom.DrawCount);

        Task secondTask = Task.Run(() => SendCombineAsync(
            second,
            SunOrbT1,
            SunOrbT1,
            clientStartUnixMs: long.MinValue));
        try
        {
            await Task.Delay(100);
            Assert.False(secondTask.IsCompleted);
            Assert.Equal(1, itemCombineRandom.DrawCount);
            Assert.Equal(2, fixture.Store.GetRequired(FirstMatchingId).Inventory.GetAllItems(SecondPlayerId).Count);
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 1], itemCombineRandom.DrawResults);
        Assert.Equal(2, itemCombineRandom.DrawCount);
        Assert.Equal(2, fixture.ItemCombineDrawCount(FirstMatchingId));
        Assert.Equal(
            SunOrbT2,
            fixture.ConnectionFor(first).DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
                Protocol.G_TO_C_ITEMS_COMBINED).OutputItemId);
        Assert.Equal(
            WindOrbT2,
            fixture.ConnectionFor(second).DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
                Protocol.G_TO_C_ITEMS_COMBINED).OutputItemId);
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
        fixture.ConnectionFor(first).BeforeSend = protocol =>
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
            fixture.ConnectionFor(second).DeliveredProtocols);
        Assert.False(firstTask.IsCompleted);

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DifferentMatches_ColoredOrbRandomStreamsAdvanceIndependently()
    {
        using var fixture = new SessionFixture();
        CountingRandom firstRandom = fixture.ConfigureItemCombineRandom(FirstMatchingId, 2);
        CountingRandom secondRandom = fixture.ConfigureItemCombineRandom(SecondMatchingId, 1);
        GameClientSession first = fixture.CreateSession(FirstMatchingId, FirstPlayerId);
        GameClientSession second = fixture.CreateSession(SecondMatchingId, SecondPlayerId);
        fixture.SeedPair(FirstMatchingId, FirstPlayerId, SunOrbT1);
        fixture.SeedPair(SecondMatchingId, SecondPlayerId, SunOrbT1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.ConnectionFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_ITEMS_COMBINED)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => SendCombineAsync(first, SunOrbT1, SunOrbT1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, firstRandom.DrawCount);

        Task secondTask = SendCombineAsync(second, SunOrbT1, SunOrbT1);
        try
        {
            await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstTask.IsCompleted);
            Assert.Equal(1, firstRandom.DrawCount);
            Assert.Equal(1, secondRandom.DrawCount);
            Assert.NotSame(firstRandom, secondRandom);
            Assert.Equal(
                WindOrbT2,
                fixture.ConnectionFor(second).DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
                    Protocol.G_TO_C_ITEMS_COMBINED).OutputItemId);
        }
        finally
        {
            release.Set();
        }

        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            WaveOrbT2,
            fixture.ConnectionFor(first).DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
                Protocol.G_TO_C_ITEMS_COMBINED).OutputItemId);
        Assert.Equal(1, fixture.ItemCombineDrawCount(FirstMatchingId));
        Assert.Equal(1, fixture.ItemCombineDrawCount(SecondMatchingId));
    }

    [Fact]
    public void SourceScope_ActivatesOnlyCombineOuterHandler()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Sessions", "GameClientSession.cs");
        string combine = ReadNormalizedSource(
            root,
            "game_server",
            "Sessions",
            "GameClientSession.ItemCombine.cs");
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Sessions",
            "GameClientSession.OrbSummon.cs");
        string playerState = ReadNormalizedSource(
            root,
            "game_server",
            "Sessions",
            "GameClientSession.PlayerState.cs");
        string clientCombine = ReadNormalizedSource(
            root,
            "client",
            "Assets",
            "Scripts",
            "GameUser.ItemCombine.cs");

        Assert.Contains(
            "Protocol.C_TO_G_COMBINE_ITEMS,\n            async bytes => await HandleMessage<C_TO_G_COMBINE_ITEMS>(bytes, HandleCombineItems)",
            session);
        Assert.Equal(1, CountOccurrences(combine, "RunUnderMatch("));
        Assert.Contains("private Task HandleCombineItemsCore(", combine);
        Assert.Contains("MatchingId <= 0", ReadMethodSlice(
            combine,
            "private Task HandleCombineItems(",
            "private Task HandleCombineItemsCore("));
        Assert.DoesNotContain("RunUnderMatch", ReadMethodSlice(
            combine,
            "private Task HandleCombineItemsCore(",
            "private bool TryHandleBattleItemCombine("));
        Assert.DoesNotContain("RunUnderMatch", ReadMethodSlice(
            orbSummon,
            "private Task HandleSummonOrb(",
            "internal bool ExecuteDraftOrbSummon("));
        Assert.DoesNotContain("RunUnderMatch", ReadMethodSlice(
            orbSummon,
            "private Task HandleDestroyOrb(",
            "private void SendDestroyOrbResult("));
        Assert.DoesNotContain("RunUnderMatch", ReadMethodSlice(
            playerState,
            "private async Task HandleUseInGameItem(",
            "private bool ApplyItemBuffs("));
        Assert.DoesNotContain("msg.ClientStartUnixMs", combine);
        Assert.Contains("ClientStartUnixMs = 0", clientCombine);
        Assert.DoesNotContain("DateTimeOffset.UtcNow", clientCombine);
    }

    private static async Task SendCombineAsync(
        GameClientSession session,
        int itemA,
        int itemB,
        long clientStartUnixMs = 0) =>
        await SendAsync(
            session,
            Protocol.C_TO_G_COMBINE_ITEMS,
            new C_TO_G_COMBINE_ITEMS
            {
                ItemA = itemA,
                ItemB = itemB,
                ClientStartUnixMs = clientStartUnixMs
            });

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

    private static void AssertCombineFailure(
        RecordingTcpConnection connection,
        int itemA,
        int itemB,
        ErrorCode errorCode)
    {
        Assert.Equal(
            [Protocol.G_TO_C_ITEMS_COMBINED, Protocol.G_TO_C_ERROR],
            connection.DeliveredProtocols);
        G_TO_C_ITEMS_COMBINED result = connection.DeserializeSingle<G_TO_C_ITEMS_COMBINED>(
            Protocol.G_TO_C_ITEMS_COMBINED);
        Assert.Equal(0, result.RecipeId);
        Assert.Equal(itemA, result.InputItemA);
        Assert.Equal(itemB, result.InputItemB);
        Assert.Equal(0, result.OutputItemId);
        Assert.Equal(string.Empty, result.OutputItemName);
        Assert.Equal(0, result.StaminaReward);
        Assert.False(result.IsRaceComplete);
        G_TO_C_ERROR error = connection.DeserializeSingle<G_TO_C_ERROR>(Protocol.G_TO_C_ERROR);
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
        private readonly Dictionary<GameClientSession, RecordingTcpConnection> _connections = [];
        private readonly ConcurrentDictionary<long, CountingRandom> _itemCombineRandoms = new();

        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-item-combine-publication-tests",
            Guid.NewGuid().ToString("N"));

        public SessionFixture()
        {
            Store = new MatchRuntimeStore(
                NullLogger.Instance,
                cleanupSteps: [new MatchCleanupStep("cleanup", _ => CleanupTimeline?.Enqueue("cleanup"))]);
            Server = CreateServer(Store);
            EventLog = Server.GetEventLogs();

        }

        public GameServer Server { get; }
        public MatchRuntimeStore Store { get; }
        public ConcurrentQueue<string>? CleanupTimeline { get; set; }
        public GameEventLogManager EventLog { get; }

        public int TotalItemCombineDraws =>
            _itemCombineRandoms.Values.Sum(random => random.DrawCount);

        public GameClientSession CreateSession(long matchingId, long playerId)
        {
            Store.GetOrCreate(matchingId);
            if (!_itemCombineRandoms.ContainsKey(matchingId))
                ConfigureItemCombineRandom(matchingId);
            // 미등록 매치는 게이트가 막는다 (#335) — 테스트 매치를 카운트다운 없이 즉시 활성으로 등록한다.
            MatchStartGate.RegisterBotOnlyMatch(matchingId);
            Store.Get(matchingId)!.Doors.Initialize();

            var connection = new RecordingTcpConnection();
            Activate(connection);
            var session = new GameClientSession(
                connection,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                TestGameSessionServices.CreateLeaveHandler(),
                static (_, _) => null,
                (_, instanceId) => _sessions
                    .Where(candidate => candidate.MatchingId == instanceId)
                    .ToList(),

                EventLog,
                new MatchResultService(Store, EventLog, new MatchSummaryFileStore(_summaryDirectory),
                    GameServerDevOptions.Disabled,
                    (_, id) => _sessions.Where(session => session.MatchingId == id).ToList(),
                    NullLogger.Instance),
                Store,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },

                new FakeGameSessionLifecycle(),
                static () => false,
                new FakeMatchEntryFailureHandler(),
                GameServerDevOptions.Disabled);
            connection.SetSession(session);
            SetIdentity(session, matchingId, playerId);
            _sessions.Add(session);
            _connections.Add(session, connection);
            return session;
        }

        public RecordingTcpConnection ConnectionFor(GameClientSession session) => _connections[session];

        /// <summary>잠금 안에서 터미널로 표시하고 나온다 — 정리는 깊이 0 탈출에서 바로 돈다.</summary>
        public void MarkTerminal(long matchingId)
        {
            MatchRuntime runtime = Store.Get(matchingId)!;
            using (Store.Enter(runtime))
            {
                Assert.True(runtime.TryMarkTerminal());
            }
        }

        public CountingRandom ConfigureItemCombineRandom(long matchingId, params int[] drawResults)
        {
            var random = new CountingRandom(drawResults);
            var runtime = Store.GetOrCreate(matchingId);
            using (Store.Enter(runtime))
            {
                // 테스트만 고정 난수를 설치한다. 운영 API에는 난수 교체 기능을 노출하지 않는다.
                typeof(SwarmMatchRuntime)
                    .GetField("<ItemCombineRandom>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(runtime.Swarm, random);
                _itemCombineRandoms[matchingId] = random;
            }
            return random;
        }

        public int ItemCombineDrawCount(long matchingId) =>
            _itemCombineRandoms.GetValueOrDefault(matchingId)?.DrawCount ?? 0;

        public (InGameItemInfo First, InGameItemInfo Second) SeedPair(
            long matchingId,
            long playerId,
            int itemId)
        {
            InGameItemInfo first = Store.GetRequired(matchingId).Inventory.AddItem(playerId, itemId);
            InGameItemInfo second = Store.GetRequired(matchingId).Inventory.AddItem(playerId, itemId);
            Assert.NotEqual(first.ItemUid, second.ItemUid);
            return (first, second);
        }

        public (long ItemUid, int ItemId, int Count)[] InventorySnapshot(
            long matchingId,
            long playerId) =>
            Store.GetRequired(matchingId).Inventory.GetAllItems(playerId)
                .OrderBy(item => item.ItemUid)
                .Select(item => (item.ItemUid, item.ItemId, item.Count))
                .ToArray();

        public void SetMatchingId(GameClientSession session, long matchingId) =>
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);

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



        private static void SetIdentity(GameClientSession session, long matchingId, long playerId)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
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

    [MessagePackObject]
    public sealed class LegacyCombineRequest
    {
        [Key("partA")] public int ItemA { get; set; }
        [Key("partB")] public int ItemB { get; set; }
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }

    private sealed class CountingRandom(params int[] drawResults) : Random
    {
        private readonly object _gate = new();
        private readonly Queue<int> _scriptedResults = new(drawResults);
        private readonly List<int> _drawResults = [];

        public int DrawCount
        {
            get
            {
                lock (_gate)
                    return _drawResults.Count;
            }
        }

        public IReadOnlyList<int> DrawResults
        {
            get
            {
                lock (_gate)
                    return _drawResults.ToArray();
            }
        }

        public override int Next(int maxValue)
        {
            lock (_gate)
            {
                int result = _scriptedResults.Count == 0 ? 0 : _scriptedResults.Dequeue();
                if (result < 0 || result >= maxValue)
                {
                    throw new InvalidOperationException(
                        $"Scripted random result {result} is outside [0, {maxValue}).");
                }

                _drawResults.Add(result);
                return result;
            }
        }
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
    }
}
