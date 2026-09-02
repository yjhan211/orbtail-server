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
using network.core;
using network.helpers;
using network.hosting;
using network.infrastructure;
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

        G_TO_C_RNG_COLLECT_ACK startAck = fixture.TokenFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK);
        Assert.Equal(ErrorCode.SUCCESS, startAck.ErrorCode);

        fixture.TokenFor(session).ClearPackets();
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
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.True(fixture.Doors.IsDoorOpen(70001, 201));
        Assert.False(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);
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

        IReadOnlyList<Protocol> protocols = fixture.TokenFor(session).DeliveredProtocols;
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
            fixture.TokenFor(session).DeliveredProtocols);
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
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.NotNull(fixture.GroundItems.GetItem(70001, item.GroundItemUid));
        G_TO_C_GROUND_ITEM_PICKUP_RESULT result = fixture.TokenFor(session)
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
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_ERROR, fixture.TokenFor(session).AttemptedProtocols);
        foreach (G_TO_C_RNG_COLLECT_ACK ack in fixture.TokenFor(session)
                     .DeserializeAll<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK))
        {
            Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        }
        Assert.Equal(
            ErrorCode.INVALID_GAME_STATE,
            fixture.TokenFor(session)
                .DeserializeSingle<G_TO_C_GROUND_ITEM_PICKUP_RESULT>(
                    Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT).ErrorCode);
    }

    [Fact]
    public async Task FinalizingAfterOuterLease_PublishesOrderedRejectionBeforeCleanup()
    {
        using var fixture = new SessionFixture();
        var timeline = new List<string>();
        fixture.AfterMessageLeaseAcquired = matchingId =>
        {
            Assert.True(fixture.Registry.TryFinalize(
                matchingId,
                static () => true,
                () =>
                {
                    timeline.Add("cleanup");
                    fixture.Coordinator.ClearMatching(matchingId);
                }));
        };
        RecordingSession session = fixture.CreateSession(70001, 101, (AreaType)50);
        fixture.TokenFor(session).BeforeSend = protocol => timeline.Add($"send:{protocol}");

        await SendAsync(
            session,
            Protocol.C_TO_G_RNG_COLLECT_START,
            new C_TO_G_RNG_COLLECT_START { InteractId = 702000101 });

        Assert.Equal(
            ["send:G_TO_C_RNG_COLLECT_ACK", "cleanup"],
            timeline);
        G_TO_C_RNG_COLLECT_ACK ack = fixture.TokenFor(session)
            .DeserializeSingle<G_TO_C_RNG_COLLECT_ACK>(Protocol.G_TO_C_RNG_COLLECT_ACK);
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, ack.ErrorCode);
        Assert.Null(fixture.Coordinator.Inspect(70001));
    }

    [Fact]
    public async Task DispatchRunsOutsideMonitor_AndTurnRetiresBeforeOuterLeaseFinalization()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.JAM_GROUND_ITEM_ID);
        var enteredDispatch = new ManualResetEventSlim();
        var releaseDispatch = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        bool? activeTurnAtCleanup = null;
        RecordingUserToken token = fixture.TokenFor(session);
        token.BeforeSend = protocol =>
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
        Assert.True(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);

        bool monitorProbeRan = false;
        Assert.True(fixture.Registry.TryExecute(70001, () => monitorProbeRan = true));
        Assert.True(monitorProbeRan);
        Assert.True(fixture.Registry.TryFinalize(
            70001,
            static () => true,
            () =>
            {
                activeTurnAtCleanup = fixture.Coordinator.Inspect(70001)?.HasActiveTurn;
                timeline.Enqueue("cleanup");
                fixture.Coordinator.ClearMatching(70001);
            },
            () => timeline.Enqueue("after")));
        Assert.DoesNotContain("cleanup", timeline);

        releaseDispatch.Set();
        await message.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(activeTurnAtCleanup);
        Assert.Equal(
            [
                "send:G_TO_C_JAM_STATE",
                "send:G_TO_C_GROUND_ITEM_REMOVED",
                "send:G_TO_C_GROUND_ITEM_PICKUP_RESULT",
                "cleanup",
                "after"
            ],
            timeline);
        Assert.Null(fixture.Coordinator.Inspect(70001));
    }

    [Fact]
    public async Task TransportFailure_DoesNotRollbackStateAndRetiresTurnBeforeGenericError()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.JAM_GROUND_ITEM_ID);
        RecordingUserToken token = fixture.TokenFor(session);
        token.ThrowOnceOn = Protocol.G_TO_C_GROUND_ITEM_REMOVED;

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
            token.AttemptedProtocols);
        Assert.DoesNotContain(Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT, token.AttemptedProtocols);
        Assert.False(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task PreparationFailure_DispatchesCapturedPrefixThenGenericErrorOutsideLane()
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
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.Null(fixture.GroundItems.GetItem(70001, item.GroundItemUid));
        Assert.True(session.CurrentCorruption < 20);
        Assert.False(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);
    }

    [Fact]
    public async Task InheritedExecutionContext_CannotReuseDisposedMessageScope()
    {
        using var fixture = new SessionFixture();
        RecordingSession session = fixture.CreateSession(70001, 101, Config.SWARM_MATCH_GROUND_AREA);
        fixture.SetCorruption(session, 20);
        GroundItemInfo item = fixture.SpawnAtSession(session, Config.HEART_GROUND_ITEM_ID);
        var releaseLateContinuation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>? lateAttempt = null;
        Func<Func<Task>, Action, Task> publishAgain = typeof(GameClientSession)
            .GetMethod(
                "PublishOrderedSessionAction",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Func<Task>, Action, Task>>(session);
        GameClientSession.SwarmHeartPickupCallback = (_, _) =>
        {
            lateAttempt = Task.Run(async () =>
            {
                await releaseLateContinuation.Task;
                try
                {
                    await publishAgain(
                        static () => Task.CompletedTask,
                        static () => { });
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });
        };

        await SendAsync(
            session,
            Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            new C_TO_G_GROUND_ITEM_PICKUP { GroundItemUid = item.GroundItemUid });
        int deliveredBeforeLateAttempt = fixture.TokenFor(session).DeliveredProtocols.Count;
        Assert.NotNull(lateAttempt);

        releaseLateContinuation.SetResult();
        Exception? failure = await lateAttempt!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(deliveredBeforeLateAttempt, fixture.TokenFor(session).DeliveredProtocols.Count);
        Assert.False(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);
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
            fixture.TokenFor(session).DeliveredProtocols);
        G_TO_C_RNG_COLLECT_RESULT result = fixture.TokenFor(session)
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
            fixture.TokenFor(session).DeliveredProtocols);
        Assert.Equal(
            stonesBefore - Config.SWARM_BOX_OPEN_COST,
            fixture.SummonStones.GetSnapshot(70001, 101).StoneCount);
        GroundItemInfo spawned = Assert.Single(fixture.GroundItems.GetSnapshot(70001, (AreaType)13));
        Assert.Contains(
            spawned.ItemId,
            new[] { Config.HEART_GROUND_ITEM_ID, Config.BOOTS_GROUND_ITEM_ID });
        G_TO_C_RNG_COLLECT_RESULT result = fixture.TokenFor(session)
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
        fixture.TokenFor(first).BeforeSend = protocol =>
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
        Assert.False(fixture.Coordinator.Inspect(70001)!.Value.HasActiveTurn);
        Assert.False(fixture.Coordinator.Inspect(70002)!.Value.HasActiveTurn);
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

        Assert.Contains("AsyncLocal<MessageMatchRuntimeScope?>", session);
        Assert.Contains("messageScope.MatchingId != matchingId", session);
        Assert.Contains("messageScope.IsDisposed", session);
        Assert.Equal(2, CountOccurrences(rng, "PublishOrderedSessionAction("));
        Assert.Equal(1, CountOccurrences(ground, "PublishOrderedSessionAction("));
        Assert.Equal(2, CountOccurrences(orbSummon, "PublishOrderedSessionAction("));
        Assert.DoesNotContain("SwarmGrowthPickCallback", session);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", session);
        Assert.DoesNotContain("SwarmGrowthPickCallback", arena);
        Assert.DoesNotContain("SwarmOrbDecisionCallback", arena);
        Assert.DoesNotContain("PublishOrderedSessionAction", doors);
        Assert.DoesNotContain("PublishOrderedSessionAction", connection);
        Assert.DoesNotContain("PublishOrderedSessionAction", arena);
        Assert.DoesNotContain("PublishOrderedSessionAction", bots);
        Assert.DoesNotContain("PublishOrderedSessionAction", botPickup);
        Assert.Contains("session.BreakDoorUnlockGauge();", arena);
        Assert.DoesNotContain(
            "PublishOrderedSessionAction",
            ReadMethodSlice(
                rng,
                "private void CancelPendingRngCollect(",
                "private void BroadcastRngCollectCooldown("));
        Assert.DoesNotContain(
            "PublishOrderedSessionAction",
            ReadMethodSlice(
                rng,
                "internal void BreakDoorUnlockGauge()",
                "private void SendRngCollectAck("));
        Assert.DoesNotContain(
            "PublishOrderedSessionAction",
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
        private readonly Dictionary<GameClientSession, RecordingUserToken> _tokens = [];
        private readonly string _summaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "orbtail-session-publication-tests",
            Guid.NewGuid().ToString("N"));

        public SessionFixture()
        {
            Server = CreateServer();
            Registry = Assert.IsType<MatchRuntimeRegistry>(
                typeof(GameServer).GetField(
                    "_matchRuntimeRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server));
            Coordinator = Assert.IsType<SwarmCombatPublicationCoordinator>(
                typeof(GameServer).GetField(
                    "_swarmCombatPublicationCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server));
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
            GameClientSession.SwarmHeartPickupCallback = null;
        }

        public GameServer Server { get; }
        public MatchRuntimeRegistry Registry { get; }
        public SwarmCombatPublicationCoordinator Coordinator { get; }
        public Action<long, Action, Action> PublishOrdered { get; }
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
        public Action<long>? AfterMessageLeaseAcquired { get; set; }

        public RecordingSession CreateSession(long matchingId, long playerId, AreaType area)
        {
            AreaStocks.InitializeMatching(matchingId);
            GroundItems.InitializeMatching(matchingId);
            Doors.InitializeMatching(matchingId);

            var token = new RecordingUserToken();
            Activate(token);
            var session = new RecordingSession(
                token,
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
                Coordinator,
                PublishOrdered,
                AcquireOperation,
                Registry);
            SetIdentity(session, matchingId, playerId, area);
            _sessions.Add(session);
            _tokens.Add(session, token);
            return session;
        }

        public RecordingUserToken TokenFor(GameClientSession session) => _tokens[session];

        public GroundItemInfo SpawnAtSession(RecordingSession session, int itemId)
        {
            GroundItemInfo item = GroundItems.SpawnItems(
                session.CurrentMapSubId,
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
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);

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

        private IDisposable? AcquireOperation(long matchingId, Action onAcquired)
        {
            IDisposable? operation = Registry.TryAcquireOperation(matchingId, onAcquired);
            if (operation != null)
                AfterMessageLeaseAcquired?.Invoke(matchingId);
            return operation;
        }

        private static void SetIdentity(
            GameClientSession session,
            long matchingId,
            long playerId,
            AreaType area)
        {
            SetProperty(session, nameof(GameClientSession.PlayerId), playerId);
            SetProperty(session, nameof(GameClientSession.CurrentMapSubId), matchingId);
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
            new ServerConfig
            {
                ServerType = "GameServer",
                ServerId = 1,
                GameServerNum = 1
            },
            null!,
            new ServerReadinessState());
    }

    private sealed class RecordingSession : GameClientSession
    {
        public RecordingSession(
            UserToken token,
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
            SwarmCombatPublicationCoordinator coordinator,
            Action<long, Action, Action> publishOrdered,
            Func<long, Action, IDisposable?> acquireOperation,
            MatchRuntimeRegistry registry)
            : base(
                token,
                null!,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                (_, matchingId) => sessions
                    .Where(session => session.CurrentMapSubId == matchingId)
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
                coordinator.TryCapturePacket,
                publishOrdered,
                static (_, publish) => publish(),
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                acquireOperation,
                registry.TryExecute,
                static (_, _, _) => { },
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                static _ => { })
        {
        }
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
