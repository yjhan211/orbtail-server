using System.Collections.Concurrent;
using System.Collections.Immutable;
using game_server;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmBotMovementCoordinatorTests
{
    [Fact]
    public void PrepareExternalMovement_FreezesMutableMovementAndRecipientOrder()
    {
        const long matchingId = 44_001;
        var position = new Vector3f(10f, 20f, 0f);
        var velocity = new Vector3f(3f, 4f, 0f);
        var fromCell = new Cell(1, 2);
        var toCell = new Cell(3, 4);
        var movement = new BotMovementEvent
        {
            BotPlayerId = -10,
            FromArea = AreaType.S2Gym1,
            ToArea = AreaType.S2Ground,
            FromCell = fromCell,
            ToCell = toCell,
            Position = position,
            Velocity = velocity,
            Rotation = 17f,
            IsAreaTransition = true
        };
        var observers = new List<SwarmBotObserverSnapshot>
        {
            new(0, 101, AreaType.S2Gym1, false, null),
            new(1, 201, AreaType.S2Ground, false, null),
            new(2, 102, AreaType.S2Gym1, false, null),
            new(3, 202, AreaType.S2Ground, false, null)
        };

        SwarmBotMovementPlan plan = CreateCoordinator().PrepareExternalMovement(
            matchingId,
            movement,
            observers);

        position.X = 999f;
        velocity.Y = 999f;
        fromCell.X = 999;
        toCell.Y = 999;
        movement.Rotation = 999f;
        observers.Clear();

        SwarmBotMovementDispatch publication = Assert.Single(plan.Movements);
        Assert.Equal(new SwarmVectorSnapshot(10f, 20f, 0f), publication.Position);
        Assert.Equal(new SwarmVectorSnapshot(3f, 4f, 0f), publication.Velocity);
        Assert.Equal(new SwarmCellSnapshot(3, 4), publication.ToCell);
        Assert.Equal(17f, publication.Rotation);
        Assert.Equal([0, 2], publication.LeaveRecipientOrdinals.ToArray());
        Assert.Equal([1, 3], publication.DestinationRecipientOrdinals.ToArray());
    }

    [Fact]
    public void PlayerInfoSnapshot_FreezesNestedCollectionsAndCell()
    {
        var source = new PlayerInfo
        {
            PlayerId = -20,
            Name = "Bot",
            WearItemIdList = [101, 202],
            Boosts = [BoostType.SPEED],
            LastCell = new Cell(7, 8)
        };

        SwarmBotPlayerInfoSnapshot snapshot = SwarmBotPlayerInfoSnapshot.Capture(source);
        source.WearItemIdList[0] = 999;
        source.Boosts.Clear();
        source.LastCell.X = 999;

        PlayerInfo firstProjection = snapshot.ToPlayerInfo();
        Assert.Equal([101, 202], firstProjection.WearItemIdList);
        Assert.Contains(BoostType.SPEED, firstProjection.Boosts);
        Assert.Equal(new Cell(7, 8), firstProjection.LastCell);

        firstProjection.WearItemIdList.Clear();
        firstProjection.Boosts.Clear();
        firstProjection.LastCell.Y = 999;
        PlayerInfo secondProjection = snapshot.ToPlayerInfo();
        Assert.Equal([101, 202], secondProjection.WearItemIdList);
        Assert.Contains(BoostType.SPEED, secondProjection.Boosts);
        Assert.Equal(new Cell(7, 8), secondProjection.LastCell);
    }

    [Fact]
    public void CapturedOrdinalLookup_PreservesOrderAndSkipsInvalidSlots()
    {
        string[] snapshot = ["first", "second", "third"];
        ImmutableArray<int> ordinals = [2, -1, 0, 99, 2];
        var resolved = new List<string>();

        foreach (int ordinal in ordinals)
        {
            if (GameServer.TryGetCapturedValue(snapshot, ordinal, out string value))
                resolved.Add(value);
        }

        Assert.Equal(["third", "first", "third"], resolved);
    }

    [Fact]
    public async Task DispatchLease_BlockedDispatchAllowsSameMatchWorkAndDefersTerminal()
    {
        const long matchingId = 44_002;
        var registry = new MatchRuntimeRegistry();
        var events = new ConcurrentQueue<string>();
        using var dispatchStarted = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        IDisposable? operation = registry.TryAcquireOperation(
            matchingId,
            () => events.Enqueue("prepare"));
        Assert.NotNull(operation);

        Task dispatchTask = Task.Run(() => GameServer.DispatchWithMatchRuntimeLease(
            operation!,
            () =>
            {
                events.Enqueue("dispatch-start");
                dispatchStarted.Set();
                Assert.True(releaseDispatch.Wait(TimeSpan.FromSeconds(5)));
                events.Enqueue("dispatch-end");
            }));

        Assert.True(dispatchStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(registry.TryExecute(
            matchingId,
            () => events.Enqueue("same-match-action")));
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Enqueue("terminal-before"),
            cleanup: () => events.Enqueue("component-cleanup"),
            afterFinalized: () => events.Enqueue("summary-after")));
        Assert.DoesNotContain("terminal-before", events);

        releaseDispatch.Set();
        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "prepare",
                "dispatch-start",
                "same-match-action",
                "dispatch-end",
                "terminal-before",
                "component-cleanup",
                "summary-after"
            ],
            events.ToArray());
    }

    [Fact]
    public void DispatchLease_AcquiredReentrantlyOutlivesOuterExecutionUntilPublication()
    {
        const long matchingId = 44_003;
        var registry = new MatchRuntimeRegistry();
        var events = new List<string>();
        IDisposable? pending = null;

        Assert.True(registry.TryExecute(
            matchingId,
            () =>
            {
                events.Add("outer-start");
                pending = registry.TryAcquireOperation(
                    matchingId,
                    () => events.Add("prepare"));
                events.Add("outer-end");
            }));
        Assert.NotNull(pending);
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Add("terminal-before"),
            cleanup: () => events.Add("component-cleanup"),
            afterFinalized: () => events.Add("summary-after")));
        Assert.DoesNotContain("terminal-before", events);

        GameServer.DispatchWithMatchRuntimeLease(
            pending!,
            () => events.Add("dispatch"));

        Assert.Equal(
            [
                "outer-start",
                "prepare",
                "outer-end",
                "dispatch",
                "terminal-before",
                "component-cleanup",
                "summary-after"
            ],
            events);
    }

    [Fact]
    public async Task PublicationTickets_PreserveCommitOrderAndTerminalWaitsForBothLeases()
    {
        const long matchingId = 44_004;
        SwarmBotMovementCoordinator coordinator = CreateCoordinator();
        var registry = new MatchRuntimeRegistry();
        var events = new ConcurrentQueue<string>();
        using var secondAttempted = new ManualResetEventSlim();
        using var firstDispatchStarted = new ManualResetEventSlim();
        using var releaseFirstDispatch = new ManualResetEventSlim();
        SwarmBotPublicationTicket firstTicket = default;
        SwarmBotPublicationTicket secondTicket = default;

        IDisposable? firstOperation = registry.TryAcquireOperation(
            matchingId,
            () =>
            {
                events.Enqueue("prepare-a");
                firstTicket = coordinator.ReservePublication(matchingId);
            });
        IDisposable? secondOperation = registry.TryAcquireOperation(
            matchingId,
            () =>
            {
                events.Enqueue("prepare-b");
                secondTicket = coordinator.ReservePublication(matchingId);
            });
        Assert.NotNull(firstOperation);
        Assert.NotNull(secondOperation);

        Task secondDispatch = Task.Run(() =>
        {
            events.Enqueue("attempt-b");
            secondAttempted.Set();
            GameServer.DispatchWithMatchRuntimeLease(
                secondOperation!,
                () => coordinator.DispatchInOrder(
                    secondTicket,
                    () => events.Enqueue("packet-b")));
        });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(secondDispatch.IsCompleted);
        Assert.DoesNotContain("packet-b", events);

        Task firstDispatch = Task.Run(() => GameServer.DispatchWithMatchRuntimeLease(
            firstOperation!,
            () => coordinator.DispatchInOrder(
                firstTicket,
                () =>
                {
                    events.Enqueue("dispatch-a-start");
                    firstDispatchStarted.Set();
                    Assert.True(releaseFirstDispatch.Wait(TimeSpan.FromSeconds(5)));
                    events.Enqueue("packet-a");
                })));
        Assert.True(firstDispatchStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(registry.TryExecute(
            matchingId,
            () => events.Enqueue("same-match-action")));
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Enqueue("terminal-before"),
            cleanup: () =>
            {
                events.Enqueue("component-cleanup");
                coordinator.ClearMatching(matchingId);
            },
            afterFinalized: () => events.Enqueue("summary-after")));
        Assert.DoesNotContain("terminal-before", events);

        releaseFirstDispatch.Set();
        await Task.WhenAll(firstDispatch, secondDispatch).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "prepare-a",
                "prepare-b",
                "attempt-b",
                "dispatch-a-start",
                "same-match-action",
                "packet-a",
                "packet-b",
                "terminal-before",
                "component-cleanup",
                "summary-after"
            ],
            events.ToArray());
    }

    [Fact]
    public async Task PublicationTickets_FirstFailureStillAdvancesWaitingSecondTurn()
    {
        const long matchingId = 44_005;
        SwarmBotMovementCoordinator coordinator = CreateCoordinator();
        SwarmBotPublicationTicket first = coordinator.ReservePublication(matchingId);
        SwarmBotPublicationTicket second = coordinator.ReservePublication(matchingId);
        using var secondAttempted = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();

        Task secondDispatch = Task.Run(() =>
        {
            secondAttempted.Set();
            coordinator.DispatchInOrder(second, () => events.Enqueue("packet-b"));
        });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(secondDispatch.IsCompleted);

        var failure = new InvalidOperationException("packet-a failed");
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            coordinator.DispatchInOrder(first, () => throw failure));
        Assert.Same(failure, thrown);
        await secondDispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["packet-b"], events.ToArray());
        coordinator.ClearMatching(matchingId);
    }

    [Fact]
    public void DispatchLease_WhenDispatchAndReleaseFail_PreservesBothFailures()
    {
        var releaseFailure = new ApplicationException("release failed");
        var dispatchFailure = new InvalidOperationException("dispatch failed");

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            GameServer.DispatchWithMatchRuntimeLease(
                new ThrowingDisposable(releaseFailure),
                () => throw dispatchFailure));

        Assert.Collection(
            exception.InnerExceptions,
            first => Assert.Same(dispatchFailure, first),
            second => Assert.Same(releaseFailure, second));
    }

    [Fact]
    public void DispatchLease_WithNoPublicationStillReleasesOperation()
    {
        var operation = new CountingDisposable();

        GameServer.DispatchWithMatchRuntimeLease(operation, static () => { });

        Assert.Equal(1, operation.DisposeCount);
    }

    [Fact]
    public void MovementSources_KeepPrepareTransportFreeAndDispatchInLegacyPacketOrder()
    {
        string root = FindRepositoryRoot();
        string coordinator = ReadNormalizedSource(
            root, "game_server", "Services", "Bots", "SwarmBotMovementCoordinator.cs");
        string server = ReadNormalizedSource(root, "game_server", "GameServer.BotMovement.cs");
        string process = ReadMethodSlice(
            server,
            "private void ProcessBotMovement(object? state)",
            "private void RecordBotMovementMetrics()");
        string dispatch = ReadMethodSlice(
            server,
            "private void DispatchSwarmBotMovementPlan(",
            "private static void SendToCapturedRecipients(");

        Assert.DoesNotContain("PacketMaker", coordinator);
        Assert.DoesNotContain("MessagePackSerializer", coordinator);
        Assert.DoesNotContain("GameClientSession", coordinator);
        Assert.DoesNotContain(".Send(", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", coordinator);
        Assert.DoesNotContain("MoveToImmutable()", server);
        AssertInOrder(
            process,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "_sessionRegistry.GetByMatch(matchingId)",
            "CaptureSwarmBotObservers(matchingId, sessionSnapshot)",
            "_swarmBotMovementCoordinator.PrepareTick(",
            "_swarmBotMovementCoordinator.ReservePublication(matchingId)",
            "DispatchWithMatchRuntimeLease(",
            "_swarmBotMovementCoordinator.DispatchInOrder(",
            "DispatchSwarmBotMovementPlan(capturedPlan, capturedSessions)");
        Assert.Contains("session.CurrentMapId == Config.SWARM_MATCH_MAP", process);
        AssertInOrder(
            process,
            "if (plan == null || sessionSnapshot == null || publicationTicket == null)",
            "SwarmBotPublicationTicket? ticketToRetire = publicationTicket;",
            "_swarmBotMovementCoordinator.DispatchInOrder(",
            "ticket,",
            "static () => { });",
            "continue;");
        Assert.DoesNotContain(".Send(", process);
        AssertInOrder(
            dispatch,
            "G_TO_C_AREA_PLAYER_LEAVE",
            "G_TO_C_AREA_PLAYER_ENTER",
            "G_TO_C_MOVE",
            "G_TO_C_ENCOUNTER_REVEAL",
            "G_TO_C_GROUND_ITEM_REMOVED",
            "G_TO_C_PLAYER_INFO");
    }

    [Fact]
    public void DummySetupAndMove_UseLeasePreparedExternalPlansWithoutOrbitAdvance()
    {
        string root = FindRepositoryRoot();
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string coordinator = ReadNormalizedSource(
            root, "game_server", "Services", "Bots", "SwarmBotMovementCoordinator.cs");
        string callback = ReadMethodSlice(
            arena,
            "GameClientSession.SwarmDummyMoveCallback ??=",
            "GameClientSession.SwarmGrowthPickCallback ??=");
        string adminSetup = ReadMethodSlice(
            arena,
            "public object SetupSwarmCutDummy(long matchingId)",
            "private PendingSwarmBotMovementDispatch? PrepareSwarmCutDummySetup(");
        string setup = ReadMethodSlice(
            arena,
            "private PendingSwarmBotMovementDispatch? PrepareSwarmCutDummySetup(",
            "private object SetupSwarmCutDummyCore(");
        string move = ReadMethodSlice(
            arena,
            "private void MoveSwarmCutDummy(long matchingId, float dirX, float dirY)",
            "private BotMovementEvent? MoveSwarmCutDummyCore(");
        string externalPrepare = ReadMethodSlice(
            coordinator,
            "public SwarmBotMovementPlan PrepareExternalMovement(",
            "private SwarmBotMovementPlan PrepareResult(");

        Assert.Contains("MoveSwarmCutDummy(dummyMatchingId, dirX, dirY);", callback);
        Assert.DoesNotContain("TryExecute", callback);
        AssertInOrder(
            adminSetup,
            "PrepareSwarmCutDummySetup(",
            "DispatchPendingSwarmBotMovement(pending);");
        AssertInOrder(
            setup,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "_sessionRegistry.GetByMatch(matchingId)",
            "SetupSwarmCutDummyCore(",
            "PrepareExternalMovement(",
            "ReservePublication(matchingId)",
            "return new PendingSwarmBotMovementDispatch(");
        AssertInOrder(
            move,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "_sessionRegistry.GetByMatch(matchingId)",
            "MoveSwarmCutDummyCore(",
            "PrepareExternalMovement(",
            "ReservePublication(matchingId)",
            "DispatchPendingSwarmBotMovement(");
        Assert.Contains("advanceOrbOrbit: false", externalPrepare);
        Assert.Contains("session.CurrentMapId == Config.SWARM_MATCH_MAP", setup);
        Assert.Contains("session.CurrentMapId == Config.SWARM_MATCH_MAP", move);
        Assert.DoesNotContain("PacketMaker", setup);
        Assert.DoesNotContain(".Send(", setup);
        Assert.DoesNotContain("PacketMaker", move);
        Assert.DoesNotContain(".Send(", move);
        Assert.DoesNotContain("BroadcastBotMovement(", arena);
    }

    [Fact]
    public void PublicationSequencer_IsClearedDuringTerminalComponentCleanup()
    {
        string root = FindRepositoryRoot();
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");

        AssertInOrder(
            server,
            "\"swarm arena\"",
            "\"bot movement publication\"",
            "_swarmBotMovementCoordinator.ClearMatching",
            "\"area closure\"",
            "\"bots\"");
    }

    [Fact]
    public void AutomaticDummyPublication_PreservesLegacyArenaOrderInsideRuntimeGate()
    {
        string root = FindRepositoryRoot();
        string proximity = ReadNormalizedSource(
            root, "game_server", "GameServer.ProximityAutoCombat.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string tick = ReadMethodSlice(
            proximity,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");
        string matching = ReadMethodSlice(
            proximity,
            "private void ProcessProximityAutoCombatForMatching(",
            "private void PrepareAndDispatchCombatPublication(");
        string publication = ReadMethodSlice(
            proximity,
            "private void PrepareAndDispatchCombatPublication(",
            "private static void AddInventoryCombatActors(");
        string automaticSetup = ReadMethodSlice(
            arena,
            "private void ProcessSwarmArenaForMatching(",
            "// 더미(#226 실험 과녁)는 웨이브 디렉터에서 제외");

        AssertInOrder(
            tick,
            "_swarmCombatPublicationCoordinator.TryBeginDueRealtimeTurn(matchingId)",
            "PrepareAndDispatchCombatPublication(",
            "() => ProcessProximityAutoCombatForMatching(matchingId, activeSessions)");
        Assert.Contains("ProcessSwarmArenaForMatching(matchingId, activeSessions);", matching);
        AssertInOrder(
            publication,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "_swarmCombatPublicationCoordinator.BeginCapture(publicationTurn)",
            "prepare();",
            "publicationPlan = capture.Freeze();");
        AssertInOrder(
            automaticSetup,
            "DEV-only order-parity exception",
            "SetupSwarmCutDummy(matchingId);",
            "foreach (var other in _botPlayerManager.GetBots(matchingId))",
            "G_TO_C_AREA_PLAYER_LEAVE(other.PlayerId)");
        Assert.DoesNotContain("capturePendingBotDispatch", proximity);
        Assert.DoesNotContain("capturePendingBotDispatch", arena);
        Assert.DoesNotContain("DispatchPendingSwarmBotMovement", tick);
    }

    private static SwarmBotMovementCoordinator CreateCoordinator() => new(
        new BotPlayerManager(NullLogger.Instance),
        new AreaClosureManager(NullLogger.Instance),
        new AreaItemStockManager(),
        new InGameInventoryManager(),
        new GroundItemManager(),
        new SummonStoneManager(),
        new EncounterRevealManager(),
        new GameEventLogManager());

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(
                marker,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"Expected '{marker}' in order.");
            previousIndex = currentIndex;
        }
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find start marker '{startMarker}'.");
        int endIndex = source.IndexOf(
            endMarker,
            startIndex + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find end marker '{endMarker}'.");
        return source[startIndex..endIndex];
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

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts)).Replace("\r\n", "\n");
    }

    private sealed class ThrowingDisposable(Exception exception) : IDisposable
    {
        public void Dispose() => throw exception;
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
