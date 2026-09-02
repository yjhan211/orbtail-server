using System.Collections.Concurrent;
using System.Reflection;
using game_server;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.contracts.authentication;
using network.core;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using network.packets;

namespace demo_regression_tests;

public sealed class MatchStartCountdownPublicationTests
{
    [Fact]
    public void PeriodicCountdown_UsesSharedOrderedTurnAndPreservesBotTickBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        string server = ReadNormalizedSource(repositoryRoot, "game_server", "GameServer.cs");
        string botMovement = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "GameServer.BotMovement.cs");
        string broadcast = ReadMethodSlice(
            server,
            "private void BroadcastMatchStartCountdowns(",
            "private void CheckHeartbeatTimeouts(");
        string botWorker = ReadMethodSlice(
            botMovement,
            "private void RunBotMovementWorker(",
            "private bool ShouldTrackBotTickBusySkip(");

        Assert.DoesNotContain("_lastMatchStartCountdownBroadcast", server);
        Assert.DoesNotContain("countdown broadcast", server);
        Assert.DoesNotContain("_matchRuntimeRegistry.TryExecute(", broadcast);
        AssertInOrder(
            broadcast,
            "MatchStartGate.IsAdmissionTimedOut(matchingId, DateTime.UtcNow)",
            "AbortMatchAfterAdmissionFailure(anchorSession);",
            "var preliminarySnapshot = MatchStartGate.GetSnapshot(matchingId);",
            "_swarmCombatPublicationCoordinator.NeedsPeriodicCountdownPublication(",
            "_swarmCombatPublicationCoordinator.BeginOrderedTurn(matchingId)",
            "if (publicationTurn == null)",
            "PrepareAndDispatchCombatPublication(",
            "var snapshot = MatchStartGate.GetSnapshot(matchingId);",
            "_swarmCombatPublicationCoordinator.TryCommitPeriodicCountdownPublication(",
            ".Where(session => session.CurrentMapSubId == matchingId)",
            "if (matchingSessions.Count == 0)",
            "Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN)",
            "MatchingId = matchingId",
            "RemainingSeconds = snapshot.RemainingSeconds",
            "ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()",
            "foreach (var session in matchingSessions)",
            "session.Send(packet);");
        Assert.Equal(2, CountOccurrences(broadcast, "MatchStartGate.GetSnapshot(matchingId)"));
        Assert.DoesNotContain("anchorSession.DisconnectForAdmissionFailure();", broadcast);

        AssertInOrder(
            botWorker,
            "BroadcastMatchStartCountdowns([matchingId], activeSessions);",
            "MatchRuntimes.TryEnter(matchingId, out MatchScope scope)",
            "ProcessBotMovementForMatching(matchingId)");
    }

    [Fact]
    public void DirectCountdownPathsAndClockOnlyStartBarrier_RemainUnchanged()
    {
        string repositoryRoot = FindRepositoryRoot();
        string connection = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Network",
            "GameClientSession.Connection.cs");
        string startGate = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Services",
            "MatchStartGate.cs");
        string connect = ReadMethodSlice(
            connection,
            "private async Task HandleConnect(C_TO_G_CONNECT msg)",
            "private async Task LoadBotsIfNeeded(");
        string directCountdown = ReadMethodSlice(
            connection,
            "private void SendMatchStartCountdown(long matchingId)",
            "private Task HandleHeartbeat()");
        string gameplayActive = ReadMethodSlice(
            startGate,
            "public static bool IsGameplayActive(long matchingId)",
            "public static bool IsAdmissionTimedOut(");

        AssertInOrder(
            connect,
            "MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);",
            "SendMatchStartCountdown(matchingId);",
            "using Packet successResponse = CreateConnectResultPacket(",
            "_executeMatchRuntime(matchingId, () =>",
            "Token.TryMarkAuthenticated(() => Volatile.Write(ref _admissionCompleted, 1))",
            "TryPublishCommittedConnectResult(successResponse)");
        AssertInOrder(
            directCountdown,
            "private void SendMatchStartCountdown(long matchingId)",
            "MatchStartGate.GetSnapshot(matchingId)",
            "Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0)",
            "Send(packet);",
            "private Task HandleMatchStartReady()",
            "MatchStartGate.MarkHumanReady(CurrentMapSubId, PlayerId.Value);",
            "SendMatchStartCountdown(CurrentMapSubId);");
        Assert.DoesNotContain("BeginOrderedTurn", connection);
        Assert.DoesNotContain("TryCommitPeriodicCountdownPublication", connection);

        AssertInOrder(
            gameplayActive,
            "state.CountdownEndsAtUtc is { } endsAt",
            "DateTime.UtcNow >= endsAt");
        Assert.DoesNotContain("PeriodicCountdown", gameplayActive);
        Assert.DoesNotContain("publication", gameplayActive, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdmissionFailure_FreezesWinningRosterAndDefersFatalDisconnectToRequiredTurn()
    {
        string repositoryRoot = FindRepositoryRoot();
        string server = ReadNormalizedSource(repositoryRoot, "game_server", "GameServer.cs");
        string method = ReadMethodSlice(
            server,
            "private void AbortMatchAfterAdmissionFailure(GameClientSession session)",
            "private bool TryAbortMatchAtTerminalBoundary(");
        string boundary = ReadMethodSlice(
            server,
            "private bool TryAbortMatchAtTerminalBoundary(",
            "private void PublishAdmissionFailureTerminalDisconnect(");
        string publication = ReadMethodSlice(
            server,
            "private void PublishAdmissionFailureTerminalDisconnect(",
            "/// <summary>\n    ///     Claims each player/subject during pre-finalization");
        string lifecyclePreparation = ReadMethodSlice(
            server,
            "private void PrepareAdmissionFailureLifecycle(",
            "private void DispatchPreparedAdmissionFailureLifecycle(");
        string lifecycleDispatch = ReadMethodSlice(
            server,
            "private void DispatchPreparedAdmissionFailureLifecycle(",
            "/// <summary>\n    ///     사람 세션 없이");

        AssertInOrder(
            method,
            "TryAbortMatchAtTerminalBoundary(",
            "_sessionRegistry.TryGetCurrent(playerId",
            "List<GameClientSession> affectedSessions = GetSessionsByMatch(matchingId);",
            "affectedSession.TryMarkMatchingLifecycleHandledExternally();",
            "session.HandoffHumanPlayerIds.Count > 0",
            "new AdmissionFailureTerminalSnapshot(");
        Assert.Equal(1, CountOccurrences(method, "GetSessionsByMatch(matchingId)"));
        AssertInOrder(
            method,
            "PublishMatchingLifecycle(",
            "MatchingLifecycleSubjects.PlayerAdmissionFailed,",
            "session.DisconnectForAdmissionFailure();");

        AssertInOrder(
            boundary,
            "AdmissionFailureTerminalSnapshot? winnerSnapshot = null;",
            "int winnerMarker = 0;",
            "winnerSnapshot = captureWinnerSnapshot();",
            "Volatile.Write(ref winnerMarker, 1);",
            "Volatile.Read(ref winnerMarker) == 0 || winnerSnapshot == null",
            "beforeLostFinalization?.Invoke(lifecyclePublications);",
            "PrepareAdmissionFailureLifecycle(",
            "PublishAdmissionFailureTerminalDisconnect(matchingId, winnerSnapshot.Sessions);",
            "DispatchPreparedAdmissionFailureLifecycle(matchingId, lifecyclePublications);");
        AssertInOrder(
            publication,
            "PublishRequiredTerminalAction(",
            "foreach (GameClientSession affectedSession in sessions)",
            "affectedSession.DisconnectForAdmissionFailure();");
        Assert.DoesNotContain("PublishMatchingLifecycle", publication);
        AssertInOrder(
            lifecyclePreparation,
            "PrepareMatchingLifecyclePublication(",
            "MatchingLifecycleSubjects.PlayerAdmissionFailed",
            "lifecyclePublications.Add(publication);");
        AssertInOrder(
            lifecycleDispatch,
            "foreach (Action publication in lifecyclePublications)",
            "publication();");
    }

    [Fact]
    public void AdmissionFailure_LosingNormalFinalizer_DefersFallbackUntilWinnerSubjectIsClaimed()
    {
        const long matchingId = 71_005;
        const long completedPlayerId = 601;
        const long excludedLatePlayerId = 602;
        GameServer server = CreateAdmissionTestServer();
        (MatchRuntimeRegistry runtimeRegistry, _) = InitializePublicationRuntime(server);
        ConfigureAdmissionCleanup(server, runtimeRegistry);
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetField("_sessionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        var completedSession = new RecordingAdmissionSession();
        SetSessionIdentity(completedSession, completedPlayerId, matchingId);
        Assert.Null(sessionRegistry.Register(completedPlayerId, completedSession, out bool completedAdded));
        Assert.True(completedAdded);
        var excludedLateSession = new RecordingAdmissionSession();
        SetSessionIdentity(excludedLateSession, excludedLatePlayerId, matchingId);
        Assert.Null(sessionRegistry.Register(excludedLatePlayerId, excludedLateSession, out bool excludedAdded));
        Assert.True(excludedAdded);

        // ReportAdmissionFailureOnce has already reserved this session marker before the normal
        // finalizer wins. The late before hook must still prepare its exact admission-failed event.
        Assert.True(completedSession.TryMarkMatchingLifecycleHandledExternally());

        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            runtimeRegistry.TryAcquireOperation(matchingId, static () => { }));
        Action? normalLifecyclePublication = null;
        Assert.True(runtimeRegistry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () =>
            {
                normalLifecyclePublication = PrepareLifecyclePublication(
                    server,
                    MatchingLifecycleSubjects.PlayerCompleted,
                    completedPlayerId,
                    matchingId);
            },
            cleanup: static () => { },
            afterFinalized: () => normalLifecyclePublication?.Invoke()));

        InvokeAdmissionAbort(server, completedSession);
        InvokeAdmissionAbort(server, excludedLateSession);

        ConcurrentDictionary<long, ConcurrentDictionary<long, string>> terminalSubjects =
            GetTerminalSubjects(server);
        Assert.False(terminalSubjects.ContainsKey(matchingId));

        operation.Dispose();

        ConcurrentDictionary<long, string> playerSubjects = terminalSubjects[matchingId];
        Assert.Equal(2, playerSubjects.Count);
        Assert.Equal(
            MatchingLifecycleSubjects.PlayerCompleted,
            playerSubjects[completedPlayerId]);
        Assert.Equal(
            MatchingLifecycleSubjects.PlayerAdmissionFailed,
            playerSubjects[excludedLatePlayerId]);
        Assert.Equal(1, completedSession.FatalCount);
        Assert.Equal(1, excludedLateSession.FatalCount);
    }

    [Fact]
    public void AdmissionFailure_AfterCompletedTombstone_PublishesExactlyOneLateFailureAndClosesAnchor()
    {
        const long matchingId = 71_007;
        const long playerId = 604;
        GameServer server = CreateAdmissionTestServer();
        (MatchRuntimeRegistry runtimeRegistry, _) = InitializePublicationRuntime(server);
        ConfigureAdmissionCleanup(server, runtimeRegistry);
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetField("_sessionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        var session = new RecordingAdmissionSession();
        SetSessionIdentity(session, playerId, matchingId);
        Assert.Null(sessionRegistry.Register(playerId, session, out bool added));
        Assert.True(added);

        Assert.True(runtimeRegistry.TryExecute(matchingId, static () => { }));
        Assert.True(runtimeRegistry.TryFinalize(
            matchingId,
            static () => true,
            cleanup: static () => { }));
        Assert.True(runtimeRegistry.IsTerminal(matchingId));

        InvokeAdmissionAbort(server, session);
        InvokeAdmissionAbort(server, session);

        ConcurrentDictionary<long, string> playerSubjects = GetTerminalSubjects(server)[matchingId];
        Assert.Single(playerSubjects);
        Assert.Equal(MatchingLifecycleSubjects.PlayerAdmissionFailed, playerSubjects[playerId]);
        Assert.Equal(1, session.FatalCount);
        Assert.Equal(1, session.DisconnectCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdmissionFailure_WhenBeforeHookIsAlreadyClosed_UsesLateFallbackAfterCommit(
        bool blockBeforeFinalized)
    {
        long matchingId = blockBeforeFinalized ? 71_008 : 71_009;
        long playerId = blockBeforeFinalized ? 605 : 606;
        GameServer server = CreateAdmissionTestServer();
        (MatchRuntimeRegistry runtimeRegistry, _) = InitializePublicationRuntime(server);
        ConfigureAdmissionCleanup(server, runtimeRegistry);
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetField("_sessionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        var session = new RecordingAdmissionSession();
        SetSessionIdentity(session, playerId, matchingId);
        Assert.Null(sessionRegistry.Register(playerId, session, out bool added));
        Assert.True(added);
        Assert.True(runtimeRegistry.TryExecute(matchingId, static () => { }));

        using var phaseEntered = new ManualResetEventSlim(initialState: false);
        using var releasePhase = new ManualResetEventSlim(initialState: false);
        Task<bool> winner = Task.Run(() => runtimeRegistry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () =>
            {
                if (blockBeforeFinalized)
                {
                    phaseEntered.Set();
                    Assert.True(releasePhase.Wait(TimeSpan.FromSeconds(5)));
                }
            },
            cleanup: () =>
            {
                if (!blockBeforeFinalized)
                {
                    phaseEntered.Set();
                    Assert.True(releasePhase.Wait(TimeSpan.FromSeconds(5)));
                }
            },
            afterFinalized: null));

        Assert.True(phaseEntered.Wait(TimeSpan.FromSeconds(2)));
        Task lateAbort = Task.Run(() => InvokeAdmissionAbort(server, session));
        releasePhase.Set();
        Assert.True(await winner.WaitAsync(TimeSpan.FromSeconds(2)));
        await lateAbort.WaitAsync(TimeSpan.FromSeconds(2));

        ConcurrentDictionary<long, string> playerSubjects = GetTerminalSubjects(server)[matchingId];
        Assert.Single(playerSubjects);
        Assert.Equal(MatchingLifecycleSubjects.PlayerAdmissionFailed, playerSubjects[playerId]);
        Assert.Equal(1, session.FatalCount);
    }

    [Fact]
    public async Task AdmissionFailure_WaitsForLeaseAndRequiredTurn_AndDisconnectsRosterExactlyOnce()
    {
        const long matchingId = 71_001;
        GameServer server = CreateAdmissionTestServer();
        var sessionRegistry = Assert.IsType<GameSessionRegistry>(
            typeof(GameServer)
                .GetField("_sessionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        (MatchRuntimeRegistry runtimeRegistry, SwarmCombatPublicationCoordinator coordinator) =
            InitializePublicationRuntime(server);
        var cleanupCoordinator = new MatchRuntimeCleanupCoordinator(
            runtimeRegistry,
            [
                new MatchRuntimeCleanupStep(
                    "session index",
                    sessionRegistry.RemoveMatch)
            ],
            NullLogger<GameServer>.Instance);
        typeof(GameServer)
            .GetField(
                "_matchRuntimeCleanupCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, cleanupCoordinator);

        var anchor = new RecordingAdmissionSession();
        SetSessionIdentity(anchor, playerId: 101, matchingId);
        var other = new RecordingAdmissionSession();
        SetSessionIdentity(other, playerId: 202, matchingId);
        Assert.Null(sessionRegistry.Register(101, anchor, out bool anchorAdded));
        Assert.True(anchorAdded);
        Assert.Null(sessionRegistry.Register(202, other, out bool otherAdded));
        Assert.True(otherAdded);
        Assert.Equal(2, sessionRegistry.GetByMatch(matchingId).Count);

        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            runtimeRegistry.TryAcquireOperation(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn inFlightTurn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        Task firstAbort = Task.Run(() => InvokeAdmissionAbort(server, anchor));
        Assert.True(SpinWait.SpinUntil(
            () => runtimeRegistry.IsTerminal(matchingId),
            TimeSpan.FromSeconds(2)));
        Task secondAbort = Task.Run(() => InvokeAdmissionAbort(server, other));
        await Task.WhenAll(firstAbort, secondAbort).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, anchor.FatalCount);
        Assert.Equal(0, other.FatalCount);

        Task releaseOperation = Task.Run(operation.Dispose);
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(0, anchor.FatalCount);
        Assert.Equal(0, other.FatalCount);

        inFlightTurn.Dispose();
        await releaseOperation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, anchor.FatalCount);
        Assert.Equal(1, anchor.DisconnectCount);
        Assert.Equal(1, other.FatalCount);
        Assert.Equal(1, other.DisconnectCount);
        Assert.Empty(sessionRegistry.GetByMatch(matchingId));
        Assert.True(runtimeRegistry.IsTerminal(matchingId));
    }

    [Fact]
    public void AdmissionDisconnect_StillBuildsFatalPacketAndRequestsGracefulClose()
    {
        string repositoryRoot = FindRepositoryRoot();
        string session = ReadNormalizedSource(
            repositoryRoot,
            "game_server",
            "Network",
            "GameClientSession.cs");
        string method = ReadMethodSlice(
            session,
            "internal virtual void DisconnectForAdmissionFailure()",
            "internal bool TryMarkMatchingLifecycleHandledExternally()");

        AssertInOrder(
            method,
            "Interlocked.Exchange(ref _admissionDisconnectIssued, 1) != 0",
            "MarkServerInitiatedDisconnect();",
            "PacketMaker.G_TO_C_ERROR(ErrorCode.FATAL",
            "Token.TrySendAndDisconnect(packet);",
            "catch (Exception ex)",
            "Token.Disconnect();");
        Assert.Equal(1, CountOccurrences(method, "Token.TrySendAndDisconnect(packet);"));
    }

    [Fact]
    public async Task PeriodicCountdown_GameServerWaitsForSharedLaneAndCapturesMatchingRecipients()
    {
        const long matchingId = 71_002;
        GameServer server = CreateAdmissionTestServer();
        (MatchRuntimeRegistry runtimeRegistry, SwarmCombatPublicationCoordinator coordinator) =
            InitializePublicationRuntime(server);
        Assert.True(runtimeRegistry.TryExecute(matchingId, static () => { }));
        MatchStartGate.RegisterHumanPlayer(
            matchingId,
            playerId: 301,
            botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
        try
        {
            var first = new RecordingAdmissionSession(coordinator);
            SetSessionIdentity(first, 301, matchingId);
            var second = new RecordingAdmissionSession(coordinator);
            SetSessionIdentity(second, 302, matchingId);
            var differentMatch = new RecordingAdmissionSession(coordinator);
            SetSessionIdentity(differentMatch, 999, matchingId + 1);
            SwarmCombatPublicationCoordinator.PublicationTurn combat =
                Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                    coordinator.TryBeginDueRealtimeTurn(matchingId));

            Task broadcast = Task.Run(() => InvokePeriodicBroadcast(
                server,
                [matchingId],
                [first, second, differentMatch]));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(0, first.SendCount);
            Assert.Equal(0, second.SendCount);

            combat.Dispose();
            await broadcast.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.SendCount);
            Assert.Equal(1, second.SendCount);
            Assert.Equal(0, differentMatch.SendCount);
            byte[] firstWire = Assert.Single(first.DeliveredWireBytes);
            byte[] secondWire = Assert.Single(second.DeliveredWireBytes);
            Assert.Equal(firstWire, secondWire);
            Assert.Equal(
                (int)Protocol.G_TO_C_MATCH_START_COUNTDOWN,
                BitConverter.ToInt32(firstWire, Config.HEADER_SIZE));
            var body = MessagePackSerializer.Deserialize<G_TO_C_MATCH_START_COUNTDOWN>(
                firstWire[(Config.HEADER_SIZE + sizeof(int) + sizeof(long))..]);
            Assert.Equal(matchingId, body.MatchingId);
            Assert.Equal(-1, body.RemainingSeconds);
            Assert.True(body.ServerUnixMs > 0);

            InvokePeriodicBroadcast(
                server,
                [matchingId],
                [first, second, differentMatch]);
            Assert.Equal(1, first.SendCount);
            Assert.Equal(1, second.SendCount);

            const long noRecipientMatchingId = 71_003;
            Assert.True(runtimeRegistry.TryExecute(noRecipientMatchingId, static () => { }));
            MatchStartGate.RegisterHumanPlayer(
                noRecipientMatchingId,
                playerId: 401,
                botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
            try
            {
                InvokePeriodicBroadcast(server, [noRecipientMatchingId], []);
                Assert.False(
                    coordinator.NeedsPeriodicCountdownPublication(
                        noRecipientMatchingId,
                        -1));
            }
            finally
            {
                MatchStartGate.RemoveMatching(noRecipientMatchingId);
            }
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    [Fact]
    public void PeriodicCountdown_GameServerFailureCommitsNoRetryAndReleasesTurn()
    {
        const long matchingId = 71_004;
        GameServer server = CreateAdmissionTestServer();
        (MatchRuntimeRegistry runtimeRegistry, SwarmCombatPublicationCoordinator coordinator) =
            InitializePublicationRuntime(server);
        Assert.True(runtimeRegistry.TryExecute(matchingId, static () => { }));
        MatchStartGate.RegisterHumanPlayer(
            matchingId,
            playerId: 501,
            botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);
        try
        {
            var failing = new RecordingAdmissionSession(
                coordinator,
                throwOnSend: true);
            SetSessionIdentity(failing, 501, matchingId);

            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(
                () => InvokePeriodicBroadcast(server, [matchingId], [failing]));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal(1, failing.SendCount);
            Assert.False(coordinator.NeedsPeriodicCountdownPublication(matchingId, -1));
            Assert.False(coordinator.Inspect(matchingId)?.HasActiveTurn);

            InvokePeriodicBroadcast(server, [matchingId], [failing]);
            Assert.Equal(1, failing.SendCount);
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    private static GameServer CreateAdmissionTestServer()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return new GameServer(
            configuration,
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

    private static (
        MatchRuntimeRegistry RuntimeRegistry,
        SwarmCombatPublicationCoordinator Coordinator)
        InitializePublicationRuntime(GameServer server)
    {
        var runtimeRegistry = Assert.IsType<MatchRuntimeRegistry>(
            typeof(GameServer)
                .GetField("_matchRuntimeRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        var coordinator = Assert.IsType<SwarmCombatPublicationCoordinator>(
            typeof(GameServer)
                .GetField(
                    "_swarmCombatPublicationCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        runtimeRegistry.SetRuntimeInitializer(
            matchingId =>
            {
                if (!coordinator.RegisterMatching(matchingId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate publication runtime for {matchingId}.");
                }
            });
        return (runtimeRegistry, coordinator);
    }

    private static void InvokePeriodicBroadcast(
        GameServer server,
        IReadOnlyCollection<long> matchingIds,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        typeof(GameServer)
            .GetMethod(
                "BroadcastMatchStartCountdowns",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [matchingIds, sessions]);
    }

    private static void InvokeAdmissionAbort(GameServer server, GameClientSession session)
    {
        typeof(GameServer)
            .GetMethod(
                "AbortMatchAfterAdmissionFailure",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [session]);
    }

    private static Action? PrepareLifecyclePublication(
        GameServer server,
        string subject,
        long playerId,
        long matchingId)
    {
        return Assert.IsType<Action?>(typeof(GameServer)
            .GetMethod(
                "PrepareMatchingLifecyclePublication",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [subject, playerId, matchingId]));
    }

    private static ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        GetTerminalSubjects(GameServer server)
    {
        return Assert.IsType<ConcurrentDictionary<long, ConcurrentDictionary<long, string>>>(
            typeof(GameServer)
                .GetField(
                    "_matchingLifecycleTerminalSubjects",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
    }

    private static void ConfigureAdmissionCleanup(
        GameServer server,
        MatchRuntimeRegistry runtimeRegistry)
    {
        typeof(GameServer)
            .GetField(
                "_matchRuntimeCleanupCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(
                server,
                new MatchRuntimeCleanupCoordinator(
                    runtimeRegistry,
                    Array.Empty<MatchRuntimeCleanupStep>(),
                    NullLogger<GameServer>.Instance));
    }

    private static object? InvokePrivate(
        GameServer server,
        string methodName,
        params object?[] args)
    {
        return typeof(GameServer)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, args);
    }

    private static void SetSessionIdentity(
        GameClientSession session,
        long playerId,
        long matchingId)
    {
        typeof(GameClientSession)
            .GetProperty(
                nameof(GameClientSession.PlayerId),
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, playerId);
        typeof(GameClientSession)
            .GetProperty(
                nameof(GameClientSession.CurrentMapSubId),
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(session, matchingId);
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{marker}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += marker.Length;
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

    private static string ReadNormalizedSource(string repositoryRoot, params string[] parts)
    {
        return File.ReadAllText(Path.Combine([repositoryRoot, .. parts]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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

    private sealed class RecordingAdmissionSession : GameClientSession
    {
        private static readonly FieldInfo AdmissionDisconnectIssuedField =
            typeof(GameClientSession).GetField(
                "_admissionDisconnectIssued",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly SwarmCombatPublicationCoordinator? _publicationCoordinator;
        private readonly bool _throwOnSend;

        public RecordingAdmissionSession(
            SwarmCombatPublicationCoordinator? publicationCoordinator = null,
            bool throwOnSend = false)
            : base(
                new UserToken(),
                null!,
                NullLogger.Instance,
                null!,
                static _ => Task.FromResult<GameHandoffContext?>(null),
                static _ => { },
                static (_, _) => null,
                static (_, _) => [],
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                static (_, _) => false,
                static (_, prepare, _) => prepare(),
                static (_, publish) => publish(),
                static (_, _, _, _) => { },
                static (_, _, _, _, _) => { },
                static _ => Random.Shared,
                static (_, _) => null,
                static (_, action) =>
                {
                    action();
                    return true;
                },
                static (_, _, _) => { },
                static (_, _) => { },
                static (_, _) => null,
                static (_, _) => { },
                static () => false,
                static _ => { })
        {
            _publicationCoordinator = publicationCoordinator;
            _throwOnSend = throwOnSend;
        }

        public int FatalCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int SendCount { get; private set; }
        public List<byte[]> DeliveredWireBytes { get; } = [];

        internal override void DisconnectForAdmissionFailure()
        {
            int wasIssued = (int)AdmissionDisconnectIssuedField.GetValue(this)!;
            base.DisconnectForAdmissionFailure();
            int isIssued = (int)AdmissionDisconnectIssuedField.GetValue(this)!;
            if (wasIssued == 0 && isIssued == 1)
            {
                FatalCount++;
                DisconnectCount++;
            }
        }

        public override void Send(IPacket packet)
        {
            if (_publicationCoordinator != null &&
                _publicationCoordinator.TryCapturePacket(RecordSend, packet))
            {
                return;
            }

            RecordSend(packet);
        }

        private void RecordSend(IPacket packet)
        {
            SendCount++;
            if (_throwOnSend)
                throw new InvalidOperationException("countdown transport failed");
            DeliveredWireBytes.Add(Assert.IsType<Packet>(packet).ToBytes());
        }
    }
}
