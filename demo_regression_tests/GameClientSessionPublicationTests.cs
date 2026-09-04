using System.Collections.Concurrent;
using System.Reflection;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.helpers;
using network.packets;
using network.utils;

namespace demo_regression_tests;

public sealed class GameClientSessionPublicationTests
{
    public GameClientSessionPublicationTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
        RngCollectCooldownStore.ClearMatching(70001);
        RngCollectCooldownStore.ClearMatching(70002);
    }

    [Fact]
    public async Task RngDoor_StartAndFinish_PreserveOrderedGaugePublication()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(
            matchingId: 70001,
            playerId: 101,
            area: (AreaType)50);

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_START,
            new C_TO_G_RNG_COLLECT_START { InteractId = 702000101 });

        G_TO_C_RNG_COLLECT_ACK startAck = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK);
        Assert.Equal(ErrorCode.SUCCESS, startAck.ErrorCode);

        fixture.ConnectionFor(session).ClearPackets();
        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_FINISH,
            new C_TO_G_RNG_COLLECT_FINISH { InteractId = 702000101 });

        Assert.Equal(
            [
                Protocol.G_TO_C_DOOR_STATE_UPDATE,
                Protocol.G_TO_C_RNG_COLLECT_RESULT,
                Protocol.G_TO_C_PLAYER_STATE
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.True(fixture.Doors.IsDoorOpen(70001, 201));
        Assert.False(Monitor.IsEntered(fixture.Store.Get(70001)!.Sync));
    }

    [Theory]
    [InlineData(Config.JAM_GROUND_ITEM_ID, Protocol.G_TO_C_JAM_STATE, 1)]
    [InlineData(Config.KEY_GROUND_ITEM_ID, Protocol.G_TO_C_FREE_SUMMON_STATE, 1)]
    [InlineData(Config.SUMMON_STONE_GROUND_ITEM_ID, Protocol.G_TO_C_SUMMON_STONE_STATE, 0)]
    [InlineData(Config.BOOTS_GROUND_ITEM_ID, null, 0)]
    [InlineData(107000010, Protocol.G_TO_C_INGAME_INVENTORY_UPDATE, 0)]
    public async Task GroundPickup_SuccessBranches_PreserveSubtypePrefixAndCommonSuffix(
        int itemId,
        Protocol? expectedPrefix,
        int expectedCounter)
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, itemId);
        int stonesBefore = fixture.SummonStones.GetSnapshot(70001, 101).StoneCount;

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });

        IReadOnlyList<Protocol> protocols = fixture.ConnectionFor(session).DeliveredProtocols;
        if (expectedPrefix.HasValue)
            Assert.Equal(expectedPrefix.Value, protocols[0]);
        Assert.Equal(Protocol.G_TO_C_GROUND_ITEM_REMOVED, protocols[^2]);
        Assert.Equal(Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT, protocols[^1]);
        Assert.Null(fixture.GroundItems.GetItem(70001, item.GroundItemUid));

        if (itemId == Config.JAM_GROUND_ITEM_ID)
            Assert.Equal(expectedCounter, session.JamCount);
        else if (itemId == Config.KEY_GROUND_ITEM_ID)
            Assert.Equal(expectedCounter, session.FreeSummonCharges);
        else if (itemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
            Assert.Equal(stonesBefore + 1, fixture.SummonStones.GetSnapshot(70001, 101).StoneCount);
        else if (itemId == 107000010)
            Assert.Contains(
                fixture.Inventories.GetAllItems(70001, 101),
                inventoryItem => inventoryItem.ItemId == itemId);
    }

    [Fact]
    public async Task GroundPickup_HeartAutoUse_CommitsStatsBeforeCommonSuffix()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetCorruption(session, 20);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });

        Assert.Equal(
            [
                Protocol.G_TO_C_PLAYER_STATS_UPDATE,
                Protocol.G_TO_C_GROUND_ITEM_REMOVED,
                Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.True(session.CurrentCorruption < 20);
    }

    [Fact]
    public async Task GroundPickup_Rejection_LeavesItemAndPublishesOnlyFailureResult()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });

        Assert.Equal(
            [Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.NotNull(fixture.GroundItems.GetItem(70001, item.GroundItemUid));
        G_TO_C_GROUND_ITEM_PICKUP_RESULT result = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_GROUND_ITEM_PICKUP_RESULT>(
                Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ITEM_NOT_USABLE, result.ErrorCode);
    }

    [Fact]
    public async Task NoMatchingRuntime_PreservesProtocolSpecificRejections()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetMatchingId(session, 0);

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_START,
            new C_TO_G_RNG_COLLECT_START { InteractId = 702000101 });
        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_FINISH,
            new C_TO_G_RNG_COLLECT_FINISH { InteractId = 702000101 });
        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = 999 });

        Assert.Equal(
            [
                Protocol.G_TO_C_RNG_COLLECT_ACK,
                Protocol.G_TO_C_RNG_COLLECT_ACK,
                Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_ERROR, fixture.ConnectionFor(session).AttemptedProtocols);
        foreach (G_TO_C_RNG_COLLECT_ACK ack in fixture.ConnectionFor(session)
                     .DeserializeAll<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK))
        {
            Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        }
        Assert.Equal(
            ErrorCode.INVALID_GAME_STATE,
            fixture.ConnectionFor(session)
                .DeserializeSingle<G_TO_C_GROUND_ITEM_PICKUP_RESULT>(
                    Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT).ErrorCode);
    }

    [Fact]
    public async Task TerminalWhileWaitingForLock_PublishesRejectionWithoutMutation()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)50);
        MatchRuntime runtime = fixture.Store.Get(70001)!;
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

        // 잠금을 기다리는 사이 매치가 끝나면 core 대신 거부 응답만 나간다.
        Task message = Task.Run(() => SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_START,
            new C_TO_G_RNG_COLLECT_START { InteractId = 702000101 }));
        await Task.Delay(100);
        Assert.False(message.IsCompleted);
        markTerminal.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await message.WaitAsync(TimeSpan.FromSeconds(5));

        G_TO_C_RNG_COLLECT_ACK ack = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK);
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        Assert.Equal([Protocol.G_TO_C_RNG_COLLECT_ACK], fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.False(fixture.Doors.IsDoorOpen(70001, 201));
        Assert.Null(fixture.Store.Get(70001));
    }

    [Fact]
    public async Task TerminalMatch_DropsLateMessagesWithoutResponse()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.MarkTerminal(70001);

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_START,
            new C_TO_G_RNG_COLLECT_START { InteractId = 702000101 });

        Assert.Empty(fixture.ConnectionFor(session).AttemptedProtocols);
        Assert.False(fixture.Doors.IsDoorOpen(70001, 201));
    }

    [Fact]
    public async Task TerminalDuringBlockedHandler_WaitsForWholeBundleThenCleansUp()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.JAM_GROUND_ITEM_ID);
        var enteredDispatch = new ManualResetEventSlim();
        var releaseDispatch = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        fixture.CleanupTimeline = timeline;
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        connection.BeforeSend = protocol =>
        {
            timeline.Enqueue($"send:{protocol}");
            if (protocol != Protocol.G_TO_C_JAM_STATE)
                return;
            enteredDispatch.Set();
            Assert.True(releaseDispatch.Wait(TimeSpan.FromSeconds(5)));
        };

        Task message = Task.Run(() => SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid }));
        Assert.True(enteredDispatch.Wait(TimeSpan.FromSeconds(5)));

        // 핸들러가 잠금을 쥔 채 송신 중이면 종료는 번들 전체가 끝날 때까지 기다린다.
        Assert.False(fixture.Store.TryEnter(70001, out _));
        Task terminal = Task.Run(() =>
        {
            fixture.MarkTerminal(70001);
            timeline.Enqueue("after");
        });
        await Task.Delay(100);
        Assert.False(terminal.IsCompleted);
        Assert.DoesNotContain("cleanup", timeline);

        releaseDispatch.Set();
        await message.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "send:G_TO_C_JAM_STATE",
                "send:G_TO_C_GROUND_ITEM_REMOVED",
                "send:G_TO_C_GROUND_ITEM_PICKUP_RESULT",
                "cleanup",
                "after"
            ],
            timeline);
        Assert.Null(fixture.Store.Get(70001));
    }

    [Fact]
    public async Task TransportFailure_DoesNotRollbackStateAndReleasesLockBeforeGenericError()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.JAM_GROUND_ITEM_ID);
        RecordingTcpConnection connection = fixture.ConnectionFor(session);
        connection.ThrowOnceOn = Protocol.G_TO_C_GROUND_ITEM_REMOVED;

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });

        Assert.Equal(1, session.JamCount);
        Assert.Null(fixture.GroundItems.GetItem(70001, item.GroundItemUid));
        Assert.Equal(
            [
                Protocol.G_TO_C_JAM_STATE,
                Protocol.G_TO_C_GROUND_ITEM_REMOVED,
                Protocol.G_TO_C_ERROR
            ],
            connection.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT, connection.AttemptedProtocols);
        Assert.False(Monitor.IsEntered(fixture.Store.Get(70001)!.Sync));
    }

    [Fact]
    public async Task PreparationFailure_KeepsAlreadySentPrefixThenGenericErrorOutsideLock()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetCorruption(session, 20);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);
        GameClientSession.SwarmHeartPickupCallback = static (_, _) =>
            throw new InvalidOperationException("heart callback failed");

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });

        Assert.Equal(
            [Protocol.G_TO_C_PLAYER_STATS_UPDATE, Protocol.G_TO_C_ERROR],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.Null(fixture.GroundItems.GetItem(70001, item.GroundItemUid));
        Assert.True(session.CurrentCorruption < 20);
    }

    [Fact]
    public async Task DormantBoxFinish_InsufficientStonesPreservesLegacyOrderedFailureSuffix()
    {
        using var fixture = new SessionFixture();
        const int interactId = 701000001;
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)13);
        int stones = fixture.SummonStones.GetSnapshot(70001, 101).StoneCount;
        Assert.True(fixture.SummonStones.TrySpendStones(70001, 101, stones, out _));
        fixture.SeedPendingFinish(session, interactId);

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_FINISH,
            new C_TO_G_RNG_COLLECT_FINISH { InteractId = interactId });

        Assert.Equal(
            [
                Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST,
                Protocol.G_TO_C_RNG_COLLECT_RESULT,
                Protocol.G_TO_C_PLAYER_STATE
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        G_TO_C_RNG_COLLECT_RESULT result = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_RESULT>(Protocol.G_TO_C_RNG_COLLECT_RESULT);
        Assert.Equal(0, result.CooldownSeconds);
        Assert.Empty(fixture.GroundItems.GetSnapshot(70001, (AreaType)13));
    }

    [Fact]
    public async Task DormantBoxFinish_SuccessPreservesStoneSpawnCooldownResultIdleOrder()
    {
        using var fixture = new SessionFixture();
        const int interactId = 701000001;
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)13);
        fixture.SeedPendingFinish(session, interactId);
        fixture.SummonStones.AddStones(70001, 101, Config.SWARM_BOX_OPEN_COST);
        int stonesBefore = fixture.SummonStones.GetSnapshot(70001, 101).StoneCount;

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_FINISH,
            new C_TO_G_RNG_COLLECT_FINISH { InteractId = interactId });

        Assert.Equal(
            [
                Protocol.G_TO_C_SUMMON_STONE_STATE,
                Protocol.G_TO_C_GROUND_ITEM_SPAWN,
                Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST,
                Protocol.G_TO_C_RNG_COLLECT_RESULT,
                Protocol.G_TO_C_PLAYER_STATE
            ],
            fixture.ConnectionFor(session).DeliveredProtocols);
        Assert.Equal(
            stonesBefore - Config.SWARM_BOX_OPEN_COST,
            fixture.SummonStones.GetSnapshot(70001, 101).StoneCount);
        GroundItemInfo spawned = Assert.Single(fixture.GroundItems.GetSnapshot(70001, (AreaType)13));
        Assert.Contains(
            spawned.ItemId,
            new[] { Config.HEART_GROUND_ITEM_ID, Config.BOOTS_GROUND_ITEM_ID });
        G_TO_C_RNG_COLLECT_RESULT result = fixture.ConnectionFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_RESULT>(Protocol.G_TO_C_RNG_COLLECT_RESULT);
        Assert.Equal(Config.SWARM_EXPLORE_REGEN_SECONDS, result.CooldownSeconds);
        Assert.NotEmpty(RngCollectCooldownStore.GetSnapshot(70001));
    }

    [Fact]
    public async Task DifferentMatches_DispatchIndependentlyWhileOneTransportIsBlocked()
    {
        using var fixture = new SessionFixture();
        RecordingSession first = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        RecordingSession second = fixture.CreateSession(70002, 202, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo firstItem = fixture.SpawnAtSession(first, Config.JAM_GROUND_ITEM_ID);
        GroundItemInfo secondItem = fixture.SpawnAtSession(second, Config.KEY_GROUND_ITEM_ID);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        fixture.ConnectionFor(first).BeforeSend = protocol =>
        {
            if (protocol != Protocol.G_TO_C_JAM_STATE)
                return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };

        Task firstTask = Task.Run(() => SendAsync(
            first,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = firstItem.GroundItemUid }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task secondTask = SendAsync(
            second,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = secondItem.GroundItemUid });
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, second.FreeSummonCharges);
        Assert.False(firstTask.IsCompleted);

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.JamCount);
    }

    [Fact]
    public void SourceScope_ActivatesOnlySelectedPlayerOuterHandlers()
    {
        string root = FindRepositoryRoot();
        string session = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.cs");
        string rng = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.RngCollect.cs");
        string ground = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.GroundItem.cs");
        string orbSummon = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.OrbSummon.cs");
        string doors = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.Doors.cs");
        string connection = ReadNormalizedSource(root, "game_server", "Network", "GameClientSession.Connection.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string bots = ReadNormalizedSource(root, "game_server", "GameServer.SwarmBots.cs");
        string botPickup = ReadNormalizedSource(
            root,
            "game_server",
            "Services",
            "Bots",
            "BotPlayerManager.ProximityAutoCombat.cs");

        Assert.DoesNotContain("AsyncLocal", session);
        Assert.Contains("protected override bool IsMessageLifecycleActive()", session);
        Assert.Contains("private Task RunUnderMatch(Func<Task> core, Action rejectIfTerminal)", session);
        Assert.Equal(2, CountOccurrences(rng, "RunUnderMatch("));
        Assert.Equal(1, CountOccurrences(ground, "RunUnderMatch("));
        Assert.Equal(2, CountOccurrences(orbSummon, "RunUnderMatch("));
        Assert.DoesNotContain("SwarmGrowthPickCallback", session);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", arena);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", arena);
        Assert.DoesNotContain("RunUnderMatch", doors);
        Assert.DoesNotContain("RunUnderMatch(", connection);
        Assert.DoesNotContain("RunUnderMatch", arena);
        Assert.DoesNotContain("RunUnderMatch", bots);
        Assert.DoesNotContain("RunUnderMatch", botPickup);
        Assert.Contains("session.BreakDoorUnlockGauge();", arena);
        Assert.DoesNotContain(
            "RunUnderMatch",
            ReadMethodSlice(
                rng,
                "private void CancelPendingRngCollect(",
                "private void BroadcastRngCollectCooldown("));
        Assert.DoesNotContain(
            "RunUnderMatch",
            ReadMethodSlice(
                rng,
                "internal void BreakDoorUnlockGauge()",
                "private void SendRngCollectAck("));
        Assert.DoesNotContain(
            "RunUnderMatch",
            ReadMethodSlice(
                ground,
                "private Task HandleDropGroundItem(",
                "internal void DropAllInventoryAtCurrentPosition("));
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
        private readonly Dictionary<GameClientSession, RecordingTcpConnection> _connections = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-session-publication-tests",
            Guid.NewGuid().ToString("N"));

        public SessionFixture()
        {
            Store = new MatchRuntimeStore(
                NullLogger.Instance,
                cleanupSteps: [new MatchCleanupStep("cleanup", _ => CleanupTimeline?.Enqueue("cleanup"))]);

            Interactables.Initialize();
            Inventories.Initialize();
            GameClientSession.SwarmHeartPickupCallback = null;
        }

        public MatchRuntimeStore Store { get; }
        public ConcurrentQueue<string>? CleanupTimeline { get; set; }
        public InteractableStateManager Interactables { get; } = new();
        public InGameInventoryManager Inventories { get; } = new();
        public AreaItemStockManager AreaStocks { get; } = new(false);
        public GroundItemManager GroundItems { get; } = new();
        public SummonStoneManager SummonStones { get; } = new();
        public DoorStateManager Doors { get; } = new();
        public MatchRosterManager Roster { get; } = new(NullLogger.Instance);
        public AreaClosureManager Closures { get; } = new(NullLogger.Instance);
        public BotPlayerManager Bots { get; } = new(NullLogger.Instance);
        public GameEventLogManager EventLog { get; } = new();
        public MatchSummaryFileStore Summaries => new(_summaryDirectory);
        public EncounterRevealManager Encounters { get; } = new();

        public RecordingSession CreateSession(long matchingId, long playerId, AreaType area)
        {
            Store.GetOrCreate(matchingId);
            AreaStocks.InitializeMatching(matchingId);
            GroundItems.InitializeMatching(matchingId);
            Doors.InitializeMatching(matchingId);

            var connection = new RecordingTcpConnection();
            Activate(connection);
            var session = new RecordingSession(
                connection,
                _sessions,
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
                Summaries,
                Encounters,
                Store);
            SetIdentity(session, matchingId, playerId, area);
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

        public GroundItemInfo SpawnAtSession(RecordingSession session, int itemId)
        {
            GroundItemInfo item = GroundItems.SpawnItems(
                session.MatchingId,
                session.CurrentArea,
                0f,
                0f,
                [itemId],
                mapId: Config.SWARM_MATCH_MAP).Single();
            SetPosition(session, new Vector3f(item.PositionX, item.PositionY, 0f));
            return item;
        }

        public void SetCorruption(RecordingSession session, int corruption) =>
            typeof(GameClientSession).GetProperty(
                "Corruption",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, corruption);

        public void SetMatchingId(RecordingSession session, long matchingId) =>
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);

        public void SeedPendingFinish(RecordingSession session, int interactId)
        {
            var pending = Assert.IsType<HashSet<int>>(
                typeof(GameClientSession).GetField(
                    "_pendingFinish",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
            pending.Add(interactId);
        }

        public void Dispose()
        {
            GameClientSession.SwarmHeartPickupCallback = null;
            if (Directory.Exists(_summaryDirectory))
                Directory.Delete(_summaryDirectory, recursive: true);
        }

        private static void SetIdentity(
            GameClientSession session,
            long matchingId,
            long playerId,
            AreaType area)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.MatchingId), matchingId);
            SetProperty(session, nameof(GameClientSession.CurrentMapId), Config.SWARM_MATCH_MAP);
            SetProperty(session, nameof(GameClientSession.CurrentArea), area);
            SetPosition(session, new Vector3f(0f, 0f, 0f));
        }

        private static void SetProperty(GameClientSession session, string name, object value) =>
            typeof(GameClientSession).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(session, value);

        private static void SetPosition(GameClientSession session, Vector3f position) =>
            typeof(GameClientSession).GetField(
                "_lastValidatedPosition",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, position);

        private static void Activate(TcpConnection connection)
        {
            int active = (int)typeof(TcpConnection).GetField(
                "StateActive",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            typeof(TcpConnection).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        }

    }

    private sealed class RecordingSession : GameClientSession
    {
        public RecordingSession(
            TcpConnection connection,
            List<GameClientSession> sessions,
            InteractableStateManager interactables,
            InGameInventoryManager inventories,
            AreaItemStockManager areaStocks,
            GroundItemManager groundItems,
            SummonStoneManager summonStones,
            DoorStateManager doors,
            MatchRosterManager roster,
            AreaClosureManager closures,
            BotPlayerManager bots,
            GameEventLogManager eventLog,
            MatchSummaryFileStore summaries,
            EncounterRevealManager encounters,
            MatchRuntimeStore matchRuntimes)
            : base(
                connection,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                (_, matchingId) => sessions
                    .Where(session => session.MatchingId == matchingId)
                    .ToList(),
                interactables,
                inventories,
                areaStocks,
                groundItems,
                summonStones,
                doors,
                roster,
                closures,
                bots,
                eventLog,
                summaries,
                encounters,
                matchRuntimes,
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                static _ => { },
                GameServerDevOptions.Disabled)
        {
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
                wireBytes = Assert.Single(
                    _delivered,
                    entry => entry.Protocol == protocol).WireBytes;

            using var packet = Packet.Create(new Const<byte[]>(wireBytes));
            Assert.Equal((int)protocol, packet.PopProtocolId());
            _ = packet.PopPlayerId();
            return MessagePackSerializer.Deserialize<T>(packet.PopBody());
        }

        public IReadOnlyList<T> DeserializeAll<T>(Protocol protocol)
        {
            List<byte[]> wirePackets;
            lock (_gate)
            {
                wirePackets = _delivered
                    .Where(entry => entry.Protocol == protocol)
                    .Select(entry => entry.WireBytes)
                    .ToList();
            }

            return wirePackets.Select(wireBytes =>
            {
                using var packet = Packet.Create(new Const<byte[]>(wireBytes));
                Assert.Equal((int)protocol, packet.PopProtocolId());
                _ = packet.PopPlayerId();
                return MessagePackSerializer.Deserialize<T>(packet.PopBody());
            }).ToList();
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
