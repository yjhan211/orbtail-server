using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using game_server;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.contracts.messaging;
using network.contracts.scaling;
using network.hosting;
using network.infrastructure;
using network.interfaces;

namespace demo_regression_tests;

public sealed class MatchSummaryPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"manitto-match-summary-persistence-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Capture_FreezesMetadataAndMutableEventGraphAcrossCleanup()
    {
        const long matchingId = 42001;
        var eventLogs = new GameEventLogManager();
        eventLogs.BeginMatch(matchingId, seed: 17);
        eventLogs.LogMatchEnded(
            matchingId,
            winnerPlayerId: 101,
            endReason: "captured-event-reason",
            tieBreakCriterion: "last_survivor",
            [new MatchFinalPlayerStats(101, 1, 120, 2, 400, 30, 6)]);
        int eventCount = eventLogs.GetForPersistence(matchingId).Count;

        MatchSummaryPersistenceRequest? request = MatchSummaryPersistence.Capture(
            eventLogs,
            NullLogger.Instance,
            matchingId,
            endReason: "captured-request-reason",
            winnerId: 101);

        Assert.NotNull(request);
        Assert.Equal(matchingId, request!.MatchingId);
        Assert.Equal("captured-request-reason", request.EndReason);
        Assert.Equal(101, request.WinnerId);
        Assert.Equal(eventCount, request.EventCount);

        GameEventEntry finalEvent = Assert.Single(
            eventLogs.GetForPersistence(matchingId),
            entry => entry.Type == "MATCH_ENDED");
        finalEvent.EndReason = "mutated-live-reason";
        finalEvent.FinalPlayerStats!.Clear();
        eventLogs.Clear(matchingId);

        var store = new MatchSummaryFileStore(_directory, maxSummaries: 5);
        MatchSummaryPersistence.Persist(request, store, NullLogger.Instance);

        MatchSummaryDocument summary = Assert.IsType<MatchSummaryDocument>(store.Read(matchingId));
        Assert.Equal("captured-request-reason", summary.EndReason);
        Assert.Equal(101, summary.WinnerPlayerId);
        Assert.Equal(eventCount, summary.RawEventCount);

        GameEventEntry persistedFinalEvent = Assert.Single(
            store.ReadRawEvents(matchingId),
            entry => entry.Type == "MATCH_ENDED");
        Assert.Equal("captured-event-reason", persistedFinalEvent.EndReason);
        MatchFinalPlayerStats persistedStats = Assert.Single(persistedFinalEvent.FinalPlayerStats!);
        Assert.Equal(101, persistedStats.PlayerId);
        Assert.Equal(6, persistedStats.OrbCount);
    }

    [Fact]
    public void Capture_WhenSnapshotFails_ReturnsNull()
    {
        MatchSummaryPersistenceRequest? request = MatchSummaryPersistence.Capture(
            null!,
            NullLogger.Instance,
            matchingId: 42002,
            endReason: "failure",
            winnerId: 0);

        Assert.Null(request);
    }

    [Fact]
    public void NormalAndNoHumanFinalization_KeepCaptureAndPostCommitPersistenceOrdering()
    {
        string root = FindRepositoryRoot();
        string sessionSource = ReadNormalizedSource(
            root,
            "game_server",
            "Network",
            "GameClientSession.MatchEnd.cs");
        string normalFinalization = ReadMethodSlice(
            sessionSource,
            "private void SendGameResult(",
            "private void PublishTerminalResult(");
        string terminalPublication = ReadMethodSlice(
            sessionSource,
            "private void PublishTerminalResult(",
            "private void RunTerminalPublicationStep(");

        int operationAcquisition = Find(normalFinalization, "_acquireMatchRuntimeOperation(");
        int resultPayload = Find(normalFinalization, ".CreateGameResultChunks(");
        int preparedTerminalPlan = Find(normalFinalization, "var preparedTerminalPlan = new MatchTerminalPublicationPlan(");
        int finalizationClaim = Find(normalFinalization, "_gameEventLogManager.TryBeginFinalization(");
        int normalFinalEvent = Find(normalFinalization, "_gameEventLogManager.LogMatchEnded(");
        int eventLogFailure = Find(normalFinalization, "Final match event logging failed;");
        int normalCapture = Find(normalFinalization, "MatchSummaryPersistence.Capture(");
        int summaryCaptureFailure = Find(normalFinalization, "Final match summary capture failed;");
        int terminalPlanCommit = Find(normalFinalization, "terminalPlan = preparedTerminalPlan;");
        int cleanupRegistration = Find(normalFinalization, "_cleanupMatchRuntime(");
        int requiredTerminalPublication = Find(
            normalFinalization,
            "_publishRequiredTerminalAction(");
        int lifecyclePlanDispatch = Find(
            normalFinalization,
            "preparedTerminalPlan.LifecyclePublications");
        int resultPublication = Find(terminalPublication, "Protocol.G_TO_C_GAME_RESULT");
        int endPublication = Find(terminalPublication, "Protocol.G_TO_C_GAME_END");
        int markEnded = Find(
            terminalPublication,
            "publication.Session.MarkGameEndedAndPrepareLifecyclePublication()");
        int lifecyclePreparationCommit = Find(
            terminalPublication,
            "plan.LifecyclePublications.Add(lifecyclePublication);");
        int lifecycleDispatch = Find(terminalPublication, "foreach (Action dispatch in lifecyclePublications)");
        int lifecycleDispatchInvocation = Find(terminalPublication, "dispatch();");
        const string persistCallPattern = @"MatchSummaryPersistence\.Persist\s*\(";
        RegexOptions sourceContractOptions =
            RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant;

        Assert.True(operationAcquisition < resultPayload);
        Assert.True(resultPayload < preparedTerminalPlan);
        Assert.True(preparedTerminalPlan < finalizationClaim);
        Assert.True(finalizationClaim < normalFinalEvent);
        Assert.True(normalFinalEvent < eventLogFailure);
        Assert.True(eventLogFailure < normalCapture);
        Assert.True(normalCapture < summaryCaptureFailure);
        Assert.True(summaryCaptureFailure < terminalPlanCommit);
        Assert.True(terminalPlanCommit < cleanupRegistration);
        Assert.True(cleanupRegistration < requiredTerminalPublication);
        Assert.True(requiredTerminalPublication < lifecyclePlanDispatch);
        Assert.True(resultPublication < endPublication);
        Assert.True(endPublication < markEnded);
        Assert.True(markEnded < lifecyclePreparationCommit);
        Assert.True(lifecyclePreparationCommit < lifecycleDispatch);
        Assert.True(lifecycleDispatch < lifecycleDispatchInvocation);
        Assert.DoesNotContain(".Send(", normalFinalization);
        Assert.DoesNotContain(".MarkGameEnded(", normalFinalization);
        Assert.Single(Regex.Matches(normalFinalization, persistCallPattern));
        Assert.Matches(
            new Regex(
                """
                finally\s*
                \{.*?
                    terminalPlan\s*=\s*preparedTerminalPlan\s*;\s*
                    MatchSummaryPersistenceRequest\?\s+capturedSummary\s*=\s*summaryRequest\s*;\s*
                    _cleanupMatchRuntime\s*
                    \(\s*
                        matchingId\s*,\s*
                        \(\s*\)\s*=>\s*_publishRequiredTerminalAction\s*
                        \(\s*
                            matchingId\s*,\s*
                            \(\s*\)\s*=>\s*PublishTerminalResult\s*
                                \(\s*preparedTerminalPlan\s*\)\s*
                        \)\s*,\s*
                        \(\s*\)\s*=>\s*
                        \{\s*
                            DispatchMatchingLifecyclePublications\s*
                            \(\s*
                                matchingId\s*,\s*
                                preparedTerminalPlan\.LifecyclePublications\s*
                            \)\s*;\s*
                            if\s*\(\s*capturedSummary\s*!=\s*null\s*\)\s*
                            \{\s*
                                MatchSummaryPersistence\.Persist\s*
                                \(\s*
                                    capturedSummary\s*,\s*
                                    _matchSummaryFileStore\s*,\s*
                                    Logger\s*
                                \)\s*;\s*
                            \}\s*
                        \}\s*
                    \)\s*;\s*
                \}
                """,
                sourceContractOptions),
            normalFinalization);
        Assert.Matches(
            new Regex(
                """
                foreach\s*\(\s*byte\[\]\s+resultPayload.*?
                    Protocol\.G_TO_C_GAME_RESULT.*?
                foreach\s*\(\s*MatchTerminalSessionPublication\s+publication.*?
                    Protocol\.G_TO_C_GAME_END.*?
                foreach\s*\(\s*MatchTerminalSessionPublication\s+publication.*?
                    publication\.Session\.MarkGameEndedAndPrepareLifecyclePublication\s*\(\s*\).*?
                    plan\.LifecyclePublications\.Add\s*\(\s*lifecyclePublication\s*\).*?
                foreach\s*\(\s*Action\s+dispatch\s+in\s+lifecyclePublications\s*\).*?
                    dispatch\s*\(\s*\)
                """,
                sourceContractOptions),
            terminalPublication);

        string publicationSource = ReadNormalizedSource(
            root,
            "game_server",
            "GameServer.ProximityAutoCombat.cs");
        string requiredTerminalAdapter = ReadMethodSlice(
            publicationSource,
            "private void PublishRequiredTerminalAction(",
            "private void PrepareAndDispatchMatchPublication(");
        int requiredTurn = Find(
            requiredTerminalAdapter,
            "_swarmCombatPublicationCoordinator.BeginRequiredTurn(matchingId)");
        int frozenPlanDispatch = Find(requiredTerminalAdapter, "publish();");
        int turnRetirement = Find(requiredTerminalAdapter, "publicationTurn.Dispose();");
        Assert.True(requiredTurn < frozenPlanDispatch);
        Assert.True(frozenPlanDispatch < turnRetirement);
        Assert.Contains("finally", requiredTerminalAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAcquireOperation", requiredTerminalAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrepareAndDispatchCombatPublication",
            requiredTerminalAdapter,
            StringComparison.Ordinal);

        string serverSource = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string lifecycleRegistration = ReadMethodSlice(
            serverSource,
            "private Action? RegisterMatchingLifecyclePublish(",
            "private async Task RunTrackedMatchingLifecyclePublishAsync(");
        int deferredDispatchFactory = Find(lifecycleRegistration, "return () =>");
        int durableDispatchStart = Find(
            lifecycleRegistration,
            "_ = RunTrackedMatchingLifecyclePublishAsync(");
        Assert.True(deferredDispatchFactory < durableDispatchStart);
        Assert.Matches(
            new Regex(
                """
                return\s*\(\s*\)\s*=>\s*
                \{.*?
                    Interlocked\.Exchange\s*\(\s*ref\s+dispatchStarted\s*,\s*1\s*\).*?
                    _\s*=\s*RunTrackedMatchingLifecyclePublishAsync\s*
                    \(\s*
                        outboxWorker\s*,\s*
                        record\s*,\s*
                        completeMatchPersistence\s*,\s*
                        completion\s*
                    \)\s*;
                """,
                sourceContractOptions),
            lifecycleRegistration);

        string noHumanFinalization = ReadMethodSlice(
            serverSource,
            "private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId, string endReason",
            "private void CleanupMatchRuntime(long matchingId)");

        int abandonedEvent = Find(noHumanFinalization, "_gameEventLogManager.LogMatchAbandoned(");
        int noHumanCapture = Find(noHumanFinalization, "MatchSummaryPersistence.Capture(");

        Assert.True(abandonedEvent < noHumanCapture);
        Assert.Single(Regex.Matches(noHumanFinalization, persistCallPattern));
        Assert.Matches(
            new Regex(
                """
                bool\s+cleanupAccepted\s*=\s*TryCleanupMatchRuntime\s*
                \(\s*
                    matchingId\s*,\s*
                    \(\s*\)\s*=>\s*!\s*HasHumanSessions\s*
                        \(\s*matchingId\s*\)\s*,\s*
                    \(\s*\)\s*=>\s*
                    \{.*?
                        summaryRequest\s*=\s*MatchSummaryPersistence\.Capture\s*
                        \(\s*
                            _gameEventLogManager\s*,\s*
                            logger\s*,\s*
                            matchingId\s*,\s*
                            endReason\s*,\s*
                            winnerPlayerId\s*
                        \)\s*;\s*
                    \}\s*,\s*
                    \(\s*\)\s*=>\s*
                    \{\s*
                        MatchSummaryPersistenceRequest\?\s+capturedSummary\s*=\s*summaryRequest\s*;\s*
                        if\s*\(\s*capturedSummary\s*!=\s*null\s*\)\s*
                        \{\s*
                            MatchSummaryPersistence\.Persist\s*
                            \(\s*
                                capturedSummary\s*,\s*
                                _matchSummaryFileStore\s*,\s*
                                logger\s*
                            \)\s*;\s*
                        \}\s*
                    \}\s*
                \)\s*;
                """,
                sourceContractOptions),
            noHumanFinalization);
        Assert.DoesNotContain("PersistMatchSummary(", noHumanFinalization);
        Assert.DoesNotContain("PublishRequiredTerminalAction", noHumanFinalization);
        Assert.DoesNotContain("BeginRequiredTurn", noHumanFinalization);

        Assert.Matches(
            new Regex(
                """
                private\s+void\s+CleanupMatchRuntime\s*
                \(\s*
                    long\s+matchingId\s*,\s*
                    Action\?\s+beforeFinalized\s*,\s*
                    Action\?\s+afterFinalized\s*
                \).*?
                TryCleanupMatchRuntime\s*
                \(\s*matchingId\s*,\s*null\s*,\s*beforeFinalized\s*,\s*afterFinalized\s*\)
                """,
                sourceContractOptions),
            serverSource);
    }

    [Fact]
    public void DurableLifecycleCompletion_SourceContract_AlwaysReleasesPendingTracker()
    {
        string root = FindRepositoryRoot();
        string serverSource = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string lifecycleRegistration = ReadMethodSlice(
            serverSource,
            "private Action? RegisterMatchingLifecyclePublish(",
            "private async Task RunTrackedMatchingLifecyclePublishAsync(");
        string trackedPublish = ReadMethodSlice(
            serverSource,
            "private async Task RunTrackedMatchingLifecyclePublishAsync(",
            "private void CompleteTrackedMatchingLifecyclePublish(");
        string completion = ReadMethodSlice(
            serverSource,
            "private void CompleteTrackedMatchingLifecyclePublish(",
            "private async Task<bool> PersistMatchingLifecycleDecisionAsync(");
        RegexOptions sourceContractOptions =
            RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant;

        Assert.Contains(
            "CompleteTrackedMatchingLifecyclePublish(",
            lifecycleRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteTrackedMatchingLifecyclePublish(",
            trackedPublish,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                """
                try\s*
                \{\s*
                    completeMatchPersistence\s*\(\s*persistenceProtected\s*\)\s*;\s*
                \}\s*
                catch\s*\(\s*Exception\s+ex\s*\)\s*
                \{.*?
                    logger\.LogCritical\s*
                    \(.*?
                        "Matching\s+lifecycle\s+persistence\s+completion\s+callback\s+failed:.*?
                    \)\s*;\s*
                \}\s*
                finally\s*
                \{.*?
                    completion\.TrySetResult\s*\(\s*true\s*\)\s*;\s*
                \}
                """,
                sourceContractOptions),
            completion);
    }

    [Fact]
    public async Task LegacyLifecycleCompletion_PrepareDefersOneShotPublishAndHoldsBarrier()
    {
        var nats = new RecordingNatsClient();
        var logger = new RecordingLogger<GameServer>();
        GameServer server = CreateLegacyGameServer(nats, logger);
        const long matchingId = 42_003;

        Action dispatch = Assert.IsType<Action>(InvokePrivate(
            server,
            "PrepareMatchingLifecyclePublication",
            MatchingLifecycleSubjects.PlayerCompleted,
            101L,
            matchingId));
        ConcurrentDictionary<long, Task> pending = GetPendingLifecycleTasks(server);
        KeyValuePair<long, Task> registration = Assert.Single(pending);
        Assert.False(registration.Value.IsCompleted);
        Assert.Equal(0, nats.PublishCount);

        Task<bool> barrier = Assert.IsAssignableFrom<Task<bool>>(InvokePrivate(
            server,
            "SealAndWaitForMatchingLifecyclePersistenceAsync",
            matchingId));
        Assert.False(barrier.IsCompleted);

        dispatch();
        dispatch();

        Assert.Equal(1, nats.PublishCount);
        Assert.True(await barrier.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(registration.Value.IsCompletedSuccessfully);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task LegacyLifecycleCompletion_PublishFailureIsLoggedButBarrierStaysSuccessful()
    {
        var nats = new RecordingNatsClient
        {
            PublishException = new InvalidOperationException("legacy failure")
        };
        var logger = new RecordingLogger<GameServer>();
        GameServer server = CreateLegacyGameServer(nats, logger);
        const long matchingId = 42_004;

        Action dispatch = Assert.IsType<Action>(InvokePrivate(
            server,
            "PrepareMatchingLifecyclePublication",
            MatchingLifecycleSubjects.PlayerCompleted,
            102L,
            matchingId));
        Task<bool> barrier = Assert.IsAssignableFrom<Task<bool>>(InvokePrivate(
            server,
            "SealAndWaitForMatchingLifecyclePersistenceAsync",
            matchingId));

        dispatch();

        Assert.Equal(1, nats.PublishCount);
        Assert.True(await barrier.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(
            logger.Contains(
                LogLevel.Error,
                "Matching lifecycle publish failed:"));
        Assert.Empty(GetPendingLifecycleTasks(server));
    }

    [Fact]
    public void LegacyLifecycle_NonCompletedWrapperStillPublishesImmediately()
    {
        var nats = new RecordingNatsClient();
        GameServer server = CreateLegacyGameServer(
            nats,
            new RecordingLogger<GameServer>());

        InvokePrivate(
            server,
            "PublishMatchingLifecycle",
            MatchingLifecycleSubjects.PlayerLeft,
            103L,
            42_005L);

        Assert.Equal(1, nats.PublishCount);
        Assert.Empty(GetPendingLifecycleTasks(server));
    }

    [Fact]
    public void LegacyLifecycle_UntrackedPreparedActionDoesNotCrossShutdownFence()
    {
        var nats = new RecordingNatsClient();
        var logger = new RecordingLogger<GameServer>();
        GameServer server = CreateLegacyGameServer(nats, logger);

        Action dispatch = Assert.IsType<Action>(InvokePrivate(
            server,
            "PrepareMatchingLifecyclePublication",
            MatchingLifecycleSubjects.PlayerLeft,
            104L,
            42_006L));
        InvokePrivate(server, "StopAcceptingLegacyMatchingLifecyclePublishes");

        dispatch();

        Assert.Equal(0, nats.PublishCount);
        Assert.True(
            logger.Contains(
                LogLevel.Warning,
                "Legacy matching lifecycle publish was skipped after the shutdown fence closed:"));
        Assert.Empty(GetPendingLifecycleTasks(server));
    }

    [Fact]
    public void LegacyLifecycleCompletion_SourceContract_DefersAndTracksOnlyPlayerCompleted()
    {
        string root = FindRepositoryRoot();
        string serverSource = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string immediateWrapper = ReadMethodSlice(
            serverSource,
            "private void PublishMatchingLifecycle(",
            "private Action? PrepareMatchingLifecyclePublication(");
        string preparation = ReadMethodSlice(
            serverSource,
            "private Action? PrepareMatchingLifecyclePublication(",
            "private bool TryRegisterMatchingLifecycleTerminal(");
        string legacyPreparation = ReadMethodSlice(
            serverSource,
            "private Action PrepareLegacyMatchingLifecyclePublication(",
            "private void PublishLegacyMatchingLifecycle(");
        string legacyPublish = ReadMethodSlice(
            serverSource,
            "private void PublishLegacyMatchingLifecycle(",
            "private void CompleteTrackedLegacyMatchingLifecyclePublish(");
        string legacyCompletion = ReadMethodSlice(
            serverSource,
            "private void CompleteTrackedLegacyMatchingLifecyclePublish(",
            "private Action? RegisterMatchingLifecyclePublish(");
        string persistenceBarrier = ReadMethodSlice(
            serverSource,
            "private async Task<bool> SealAndWaitForMatchingLifecyclePersistenceAsync(",
            "private void MarkMatchingLifecycleOwnerReleaseCompleted(");
        int immediatePreparation = Find(
            immediateWrapper,
            "Action? dispatch = PrepareMatchingLifecyclePublication(subject, playerId, matchingId);");
        int immediateDispatch = Find(immediateWrapper, "dispatch?.Invoke();");
        Assert.True(immediatePreparation < immediateDispatch);
        Assert.DoesNotContain(
            "PublishLegacyMatchingLifecycle(",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "return PrepareLegacyMatchingLifecyclePublication(subject, playerId, matchingId);",
            preparation,
            StringComparison.Ordinal);

        int playerCompletedGuard = Find(
            legacyPreparation,
            "if (subject == MatchingLifecycleSubjects.PlayerCompleted)");
        int registrationGate = Find(legacyPreparation, "lock (_matchingLifecycleEnqueueGate)");
        int persistenceRegistration = Find(
            legacyPreparation,
            "TryBeginMatchingLifecyclePersistenceUnderGate(matchingId)");
        int completionSource = Find(
            legacyPreparation,
            "new TaskCompletionSource<bool>(");
        int pendingRegistration = Find(
            legacyPreparation,
            "_pendingMatchingLifecyclePublishTasks.TryAdd(operationId, completion.Task);");
        int deferredFactory = Find(legacyPreparation, "return () =>");
        int exactlyOnceGuard = Find(
            legacyPreparation,
            "Interlocked.Exchange(ref dispatchStarted, 1)");
        int legacyAttempt = Find(
            legacyPreparation,
            "PublishLegacyMatchingLifecycle(subject, playerId, matchingId);");
        int completionFinally = Find(legacyPreparation, "finally");
        int trackedCompletion = Find(
            legacyPreparation,
            "CompleteTrackedLegacyMatchingLifecyclePublish(");

        Assert.True(playerCompletedGuard < registrationGate);
        Assert.True(registrationGate < persistenceRegistration);
        Assert.True(persistenceRegistration < completionSource);
        Assert.True(completionSource < pendingRegistration);
        Assert.True(pendingRegistration < deferredFactory);
        Assert.True(deferredFactory < exactlyOnceGuard);
        Assert.True(exactlyOnceGuard < legacyAttempt);
        Assert.True(legacyAttempt < completionFinally);
        Assert.True(completionFinally < trackedCompletion);
        Assert.Single(
            Regex.Matches(
                legacyPreparation,
                Regex.Escape("TryBeginMatchingLifecyclePersistenceUnderGate(matchingId)")));
        Assert.Contains("logger.LogError(", legacyPublish, StringComparison.Ordinal);

        int successfulAttemptCompletion = Find(
            legacyCompletion,
            "completeMatchPersistence(true);");
        int completionFailureLog = Find(
            legacyCompletion,
            "Legacy matching lifecycle completion callback failed:");
        int trackerCompletion = Find(
            legacyCompletion,
            "completion.TrySetResult(true);");
        int exactTrackerRemoval = Find(
            legacyCompletion,
            ".Remove(new KeyValuePair<long, Task>(operationId, completion.Task));");
        Assert.True(successfulAttemptCompletion < completionFailureLog);
        Assert.True(completionFailureLog < trackerCompletion);
        Assert.True(trackerCompletion < exactTrackerRemoval);

        int existingStateLookup = Find(
            persistenceBarrier,
            "_matchingLifecyclePersistenceStates.TryGetValue(");
        int legacyNoStateFastPath = Find(
            persistenceBarrier,
            "if (!IsDurableMatchingLifecycleEnabled)");
        int durableStateCreation = Find(
            persistenceBarrier,
            "state = _matchingLifecyclePersistenceStates.GetOrAdd(");
        int barrierWait = Find(persistenceBarrier, "await waitTask;");
        int barrierResult = Find(
            persistenceBarrier,
            "return !state.PersistenceFailed;");
        Assert.True(existingStateLookup < legacyNoStateFastPath);
        Assert.True(legacyNoStateFastPath < durableStateCreation);
        Assert.True(durableStateCreation < barrierWait);
        Assert.True(barrierWait < barrierResult);
        Assert.Contains("await waitTask;", persistenceBarrier, StringComparison.Ordinal);
        Assert.Contains("return !state.PersistenceFailed;", persistenceBarrier, StringComparison.Ordinal);

        string untrackedPublish = ReadMethodSlice(
            serverSource,
            "private void PublishUntrackedLegacyMatchingLifecycle(",
            "private void PublishLegacyMatchingLifecycle(");
        int untrackedGate = Find(
            untrackedPublish,
            "lock (_matchingLifecycleEnqueueGate)");
        int shutdownFence = Find(
            untrackedPublish,
            "Volatile.Read(ref _acceptingLegacyMatchingLifecyclePublishes) == 0");
        int fencedPublish = Find(
            untrackedPublish,
            "PublishLegacyMatchingLifecycle(subject, playerId, matchingId);");
        Assert.True(untrackedGate < shutdownFence);
        Assert.True(shutdownFence < fencedPublish);

        int legacyFenceStart = Find(
            serverSource,
            "StartAcceptingLegacyMatchingLifecyclePublishes();");
        int natsClientCreation = Find(
            serverSource,
            "_matchingLifecycleNatsClient = natsClientFactory.Create();");
        Assert.True(natsClientCreation < legacyFenceStart);
        string shutdown = ReadMethodSlice(
            serverSource,
            "public async Task StopAsync(",
            "private async Task WaitForScalingDrainAsync(");
        int lateOwnerLossDrain = Find(
            shutdown,
            "late match owner loss");
        int legacyFenceStop = Find(
            shutdown,
            "StopAcceptingLegacyMatchingLifecyclePublishes();");
        int finalLifecycleDrain = Find(
            shutdown,
            "late matching lifecycle outbox persistence");
        Assert.True(lateOwnerLossDrain < legacyFenceStop);
        Assert.True(legacyFenceStop < finalLifecycleDrain);
    }

    [Fact]
    public void RedisCleanup_SourceContract_RegistersBeforeDeferredDispatchAndAlwaysCompletesTracker()
    {
        string root = FindRepositoryRoot();
        string serverSource = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string preparation = ReadMethodSlice(
            serverSource,
            "private Action PrepareMatchingRedisCleanup(",
            "private async Task RunTrackedMatchingRedisCleanupAsync(");
        string trackedCleanup = ReadMethodSlice(
            serverSource,
            "private async Task RunTrackedMatchingRedisCleanupAsync(",
            "private void CompleteMatchingRedisCleanup(");
        string completion = ReadMethodSlice(
            serverSource,
            "private void CompleteMatchingRedisCleanup(",
            "private async Task WaitForPendingMatchingRedisCleanupsAsync(");

        int completionSource = Find(preparation, "new TaskCompletionSource<bool>(");
        int pendingRegistration = Find(
            preparation,
            "_pendingMatchingRedisCleanupTasks.TryAdd(operationId, completion.Task)");
        int deferredFactory = Find(preparation, "return () =>");
        int exactlyOnceGuard = Find(
            preparation,
            "Interlocked.Exchange(ref dispatchStarted, 1)");
        int trackedDispatch = Find(
            preparation,
            "_ = RunTrackedMatchingRedisCleanupAsync(");

        Assert.True(completionSource < pendingRegistration);
        Assert.True(pendingRegistration < deferredFactory);
        Assert.True(deferredFactory < exactlyOnceGuard);
        Assert.True(exactlyOnceGuard < trackedDispatch);
        Assert.DoesNotContain(
            "CleanupAbandonedMatchingRedisAsync(",
            preparation,
            StringComparison.Ordinal);

        int cleanupInvocation = Find(
            trackedCleanup,
            "await CleanupAbandonedMatchingRedisAsync(matchingId);");
        int cleanupFinally = Find(trackedCleanup, "finally");
        int trackedCompletion = Find(
            trackedCleanup,
            "CompleteMatchingRedisCleanup(operationId, completion);");
        Assert.True(cleanupInvocation < cleanupFinally);
        Assert.True(cleanupFinally < trackedCompletion);

        int completionSignal = Find(completion, "completion.TrySetResult(true);");
        int pendingRemoval = Find(
            completion,
            "_pendingMatchingRedisCleanupTasks)");
        Assert.True(completionSignal < pendingRemoval);
        Assert.Matches(
            new Regex(
                """
                try\s*
                \{\s*
                    completion\.TrySetResult\s*\(\s*true\s*\)\s*;\s*
                \}\s*
                finally\s*
                \{.*?
                    _pendingMatchingRedisCleanupTasks.*?
                    Remove\s*\(\s*new\s+KeyValuePair<long,\s*Task>\s*
                    \(\s*operationId\s*,\s*completion\.Task\s*\)\s*\)\s*;\s*
                \}
                """,
                RegexOptions.Singleline |
                RegexOptions.IgnorePatternWhitespace |
                RegexOptions.CultureInvariant),
            completion);

        Assert.DoesNotContain(
            "\"Redis cleanup scheduling\"",
            serverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "logger,\n            PrepareMatchingRedisCleanup);",
            serverSource,
            StringComparison.Ordinal);

        string coordinatorSource = ReadNormalizedSource(
            root,
            "game_server",
            "Services",
            "MatchRuntimeCleanupCoordinator.cs");
        int winnerDispatch = Find(
            coordinatorSource,
            "\"match winner post-finalization\",");
        int callerAfter = Find(
            coordinatorSource,
            "\"match post-finalization\",");
        Assert.True(winnerDispatch < callerAfter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static int Find(string source, string marker)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find source marker '{marker}'.");
        return index;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find method start marker '{startMarker}'.");
        int endIndex = source.IndexOf(
            endMarker,
            startIndex + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find method end marker '{endMarker}'.");
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

    private GameServer CreateLegacyGameServer(
        INatsClient nats,
        ILogger<GameServer> logger)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MATCH_SUMMARY_DIRECTORY"] = _directory,
                ["MATCH_SUMMARY_MAX_FILES"] = "5",
                ["userServerScaling:enabled"] = "false"
            })
            .Build();
        var server = new GameServer(
            configuration,
            logger,
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
        typeof(GameServer)
            .GetField(
                "_matchingLifecycleNatsClient",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, nats);
        InvokePrivate(server, "StartAcceptingLegacyMatchingLifecyclePublishes");
        return server;
    }

    private static object? InvokePrivate(
        GameServer server,
        string methodName,
        params object?[] args) =>
        typeof(GameServer)
            .GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, args);

    private static ConcurrentDictionary<long, Task> GetPendingLifecycleTasks(
        GameServer server) =>
        Assert.IsType<ConcurrentDictionary<long, Task>>(
            typeof(GameServer)
                .GetField(
                    "_pendingMatchingLifecyclePublishTasks",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));

    private sealed class RecordingNatsClient : INatsClient
    {
        public int PublishCount { get; private set; }
        public Exception? PublishException { get; init; }

        public void Publish(string subject, byte[] message)
        {
            PublishCount++;
            if (PublishException != null)
                throw PublishException;
        }

        public void Subscribe(
            string subject,
            Action<string, byte[]> messageHandler) =>
            throw new NotSupportedException();

        public Task<byte[]> RequestAsync(
            string subject,
            byte[] message,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void SubscribeRequest(
            string subject,
            Func<string, byte[], CancellationToken, Task<byte[]>> messageHandler,
            string? queue = null) =>
            throw new NotSupportedException();

        public void EnsureDurableStream(NatsDurableStreamOptions options) =>
            throw new NotSupportedException();

        public Task<NatsDurablePublishAck> PublishDurableAsync(
            string stream,
            string subject,
            string messageId,
            byte[] message,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void SubscribeDurableQueue(
            NatsDurableConsumerOptions options,
            Func<NatsDurableMessage, CancellationToken, Task<NatsDurableMessageDisposition>>
                messageHandler) =>
            throw new NotSupportedException();

        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Close()
        {
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        public bool Contains(LogLevel level, string text) =>
            _entries.Any(entry =>
                entry.Level == level &&
                entry.Message.Contains(text, StringComparison.Ordinal));
    }
}
