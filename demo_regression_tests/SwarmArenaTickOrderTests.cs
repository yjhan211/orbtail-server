namespace demo_regression_tests;

public sealed class SwarmArenaTickOrderTests
{
    [Fact]
    public void ProximityAutoCombatTimer_UsesFiftyMillisecondMatchRuntimeGate()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(
            root, "game_server", "GameServer.ProximityAutoCombat.cs");

        Assert.Contains("private const int ProximityAutoCombatTickIntervalMs = 50;", source);
        Assert.Contains("ProcessProximityAutoCombatTick,", source);
        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs)"));

        string tickBody = ReadMethodSlice(
            source,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");
        AssertInOrder(
            tickBody,
            "_sessionRegistry.SnapshotWhere(",
            "GetActiveMatchingIds()",
            "_matchRuntimeRegistry.TryExecute(",
            "ProcessProximityAutoCombatForMatching(");

        string matchingBody = ReadMethodSlice(
            source,
            "private void ProcessProximityAutoCombatForMatching(",
            "private static void AddInventoryCombatActors(");
        Assert.Contains(
            "ProcessSwarmArenaForMatching(matchingId, activeSessions);",
            matchingBody);
    }

    [Fact]
    public void SwarmArenaTick_RunsDirectorBeforeGameplayGate()
    {
        string arenaTick = ReadSwarmArenaTick();

        AssertInOrder(
            arenaTick,
            "_swarmMonsterDirector.Tick(",
            "if (!MatchStartGate.IsGameplayActive(matchingId))",
            "BroadcastMonsterMinimapSnapshot(",
            "return;");
    }

    [Fact]
    public void SwarmArenaTick_KeepsAuthoritativeGameplayPhaseOrder()
    {
        string arenaTick = ReadSwarmArenaTick();

        AssertInOrder(
            arenaTick,
            "UpdateSwarmOrbTrails(",
            "ProcessSwarmTrailCuts(",
            "ProcessSwarmRetaliationWindows(",
            "ProcessSwarmEncirclements(",
            "ProcessSwarmWaveBombs(",
            "ProcessSwarmWindBlades(",
            "ProcessSwarmSunBurns(",
            "ApplySwarmParticipantDamage(",
            "ProcessSwarmBotRecovery(",
            "ProcessSwarmSleepRecovery(",
            "ProcessSwarmBotExplores(",
            "ProcessSwarmBotDoorUnlocks(",
            "UpdateSwarmMovementSamples(",
            "BuildSwarmArenaCombatActors(",
            "ProcessSwarmPvpAttackEvents(",
            "ProcessOrbRecovery(",
            "CommitAndDispatchOrbVisualStatePublications(",
            "BroadcastSwarmOrbRankings(",
            "ProcessSwarmGrowthOffers(",
            "ProcessSwarmScoreTimeout(",
            "ProcessPendingSwarmMonsterHits(",
            "ProcessSwarmCrossfires(",
            "ApplySwarmPvpAttack(matchingId, pending.Attack",
            "CollectSwarmCrossfireCappedOwners(",
            "CollectSwarmCrossfireAnchoredTargets(",
            "_proximityAutoCombatResolver.Resolve(",
            "TryScheduleSwarmCrossfire(",
            "_botPlayerManager.TryFinalizeProximityAutoCombatElimination(");
    }

    [Fact]
    public void SwarmPvpAttackEventTick_DrainsDueWorkBeforeLaunchingAndStartingEvents()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmAttackEvents.cs");
        string processBody = ReadMethodSlice(
            source,
            "private void ProcessSwarmPvpAttackEvents(",
            "private SpotArenaPlayerSpatial? ResolveSwarmAttackTarget(");

        AssertInOrder(
            processBody,
            "DispatchPendingSwarmAttackVisuals(",
            "ApplyPendingSwarmAttackHits(",
            "LaunchReadySwarmAttackEvents(",
            "foreach (var owner in participants)");
    }

    [Fact]
    public void GrowthOfferFlow_DelegatesLifecycleAndOwnershipToCoordinator()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string tickBody = ReadMethodSlice(
            source,
            "private void ProcessSwarmGrowthOffers(",
            "private int GetSwarmTopOrbCount(");
        string pickBody = ReadMethodSlice(
            source,
            "internal void HandleSwarmGrowthPick(",
            "private bool ApplySwarmGrowthCard(");

        AssertInOrder(
            tickBody,
            ".EvaluateStanding(",
            ".EvaluateFunding(",
            ".AllocateOfferId()",
            ".RegisterOffer(");
        Assert.Contains(".TryApplyPick(", pickBody);
        Assert.DoesNotContain(".GrowthOffers.Offers", tickBody);
        Assert.DoesNotContain(".GrowthOffers.Offers", pickBody);
    }

    [Fact]
    public void SwarmCleanup_AlwaysDropsMatchOwnedRuntimeAfterMonsterCleanup()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string cleanupBody = ReadMethodSlice(
            source,
            "private void CleanupSwarmArenaState(",
            "private List<ProximityCombatActor> BuildSwarmArenaCombatActors(");

        AssertInOrder(
            cleanupBody,
            "try",
            "_swarmMonsterDirector.RemoveMatching(matchingId);",
            "finally",
            "_swarmMatchRuntimes.Remove(matchingId);");
        Assert.DoesNotContain("ClearSwarmCrossfireState", cleanupBody);
        Assert.DoesNotContain("CleanupSwarmPvpAttackEvents", cleanupBody);
        Assert.DoesNotContain("ClearSwarmWindBladeState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmOrbBoardState", cleanupBody);
    }

    [Fact]
    public void ScheduledClosureTick_CommitsStateBeforeTransportAndPublishesOutsideMatchMonitor()
    {
        string root = FindRepositoryRoot();
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string coordinator = ReadNormalizedSource(
            root,
            "game_server",
            "Services",
            "Field",
            "SwarmClosurePublicationCoordinator.cs");
        string tick = ReadMethodSlice(
            server,
            "private void ProcessAreaClosureTick(object? state)",
            "// ===== 타겟 위치 전송 =====");
        string prepare = ReadMethodSlice(
            arena,
            "private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(",
            "private static ImmutableArray<int> CaptureSwarmClosureRecipientOrdinals(");
        string dispatch = ReadMethodSlice(
            arena,
            "private void DispatchSwarmClosurePublicationPlan(",
            "private void PrepareDestroySwarmOrbsInClosedAreas(");
        string orbPrepare = ReadMethodSlice(
            arena,
            "private void PrepareDestroySwarmOrbsInClosedAreas(",
            "// 쌍 깔때기:");

        AssertInOrder(
            tick,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "sessionSnapshot = GetSessionsByMatch(matchingId).ToArray();",
            "plan = PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);",
            "_swarmClosurePublicationCoordinator.ReservePublication(matchingId)",
            "DispatchWithMatchRuntimeLease(",
            "_swarmClosurePublicationCoordinator.DispatchInOrder(",
            "DispatchSwarmClosurePublicationPlan(capturedPlan, capturedSessions)");
        Assert.DoesNotContain("_matchRuntimeRegistry.TryExecute(", tick);
        Assert.DoesNotContain(".Send(", tick);

        AssertInOrder(
            prepare,
            "_areaClosureManager.InitializeMatching(",
            "new SwarmFieldStateOutbound(",
            "_areaClosureManager.CheckClosureSchedule(matchingId)",
            "new SwarmClosureWarningOutbound(",
            "_gameEventLogManager.LogClosure(",
            "new SwarmAreaClosedOutbound(",
            "_doorStateManager.CloseDoorsForAreas(",
            "new SwarmDoorStateOutbound(",
            "PrepareDestroySwarmOrbsInClosedAreas(",
            "new SwarmClosurePublicationPlan(");
        Assert.DoesNotContain("Packet.Create(", prepare);
        Assert.DoesNotContain("PacketMaker.", prepare);
        Assert.DoesNotContain(".Send(", prepare);
        Assert.Contains("Transport failure never rolls back", arena);

        AssertInOrder(
            orbPrepare,
            "DestroySwarmOrbsFromOrdinal(",
            ".OrbCutCracks.Remove(",
            ".OrbDurabilityBonus.Remove(",
            "new SwarmInventoryUpdateOutbound(",
            "new SwarmRingVfxOutbound(",
            "_gameEventLogManager.LogSystem(");
        Assert.DoesNotContain("Packet.Create(", orbPrepare);
        Assert.DoesNotContain("PacketMaker.", orbPrepare);
        Assert.DoesNotContain(".Send(", orbPrepare);
        Assert.DoesNotContain("SendInGameInventoryUpdate(", orbPrepare);
        Assert.DoesNotContain("SendSwarmRingVfx(", orbPrepare);

        AssertInOrder(
            dispatch,
            "case SwarmFieldStateOutbound",
            "Protocol.G_TO_C_SWARM_FIELD_STATE",
            "case SwarmClosureWarningOutbound",
            "Protocol.G_TO_C_AREA_CLOSURE_WARNING",
            "case SwarmAreaClosedOutbound",
            "Protocol.G_TO_C_AREA_CLOSED",
            "case SwarmDoorStateOutbound",
            "PacketMaker.G_TO_C_DOOR_STATE_UPDATE(",
            "case SwarmInventoryUpdateOutbound",
            "session.SendInGameInventoryUpdate(",
            "case SwarmRingVfxOutbound",
            "Protocol.G_TO_C_SWARM_ENCIRCLE_VFX");
        Assert.Contains("SendToCapturedRecipients(", dispatch);
        Assert.DoesNotContain("catch", dispatch);
        Assert.Contains("first transport exception", arena);
        Assert.Contains("finally", coordinator);
        Assert.Contains("state.ServingTicket++;", coordinator);
        Assert.Contains("state.DispatchingTicket = ticket.Sequence;", coordinator);
    }

    [Fact]
    public void ScheduledClosurePublicationCleanup_PrecedesAreaStateCleanup()
    {
        string root = FindRepositoryRoot();
        string server = ReadNormalizedSource(root, "game_server", "GameServer.cs");

        AssertInOrder(
            server,
            "\"bot movement publication\"",
            "_swarmBotMovementCoordinator.ClearMatching",
            "\"field closure publication\"",
            "_swarmClosurePublicationCoordinator.ClearMatching",
            "\"area closure\"",
            "_areaClosureManager.CleanupMatching");
    }

    [Fact]
    public void WindBladeAndOrbBoardState_AreOwnedBySwarmMatchRuntime()
    {
        string root = FindRepositoryRoot();
        string windBlade = ReadNormalizedSource(root, "game_server", "GameServer.SwarmWindBlade.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");
        string orbBoard = ReadNormalizedSource(root, "game_server", "GameServer.SwarmOrbBoard.cs");

        Assert.DoesNotContain("_swarmWindBladeNextTickAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeEngagedAtUtc", windBlade);
        Assert.DoesNotContain("_swarmWindBladeVictimImmuneUntilUtc", windBlade);
        Assert.DoesNotContain("_swarmWindWoundsUntilUtc", crossfire);
        Assert.DoesNotContain("_swarmFamilyUpgradeCounts", orbBoard);
        Assert.Contains("GetSwarmMatchRuntime(matchingId).WindBlade", windBlade);
        Assert.Contains("GetSwarmMatchRuntime(matchingId).OrbBoard", orbBoard);
    }

    [Fact]
    public void SwarmCrossfireState_IsMatchOwnedAndDodgeLookupDoesNotCreateRuntime()
    {
        string root = FindRepositoryRoot();
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");
        string botDodge = ReadNormalizedSource(root, "game_server", "GameServer.SwarmBotDodge.cs");
        string runtimeStates = ReadNormalizedSource(root, "game_server", "Services", "SwarmArenaStates.cs");

        Assert.DoesNotContain("_swarmCrossfireShapes", crossfire);
        Assert.DoesNotContain("_swarmCrossfireDodgeSnapshot", crossfire);
        Assert.DoesNotContain("_swarmCrossfireEventSeq", crossfire);
        Assert.DoesNotContain("_swarmSunBurns", crossfire);
        Assert.DoesNotContain("_swarmCrossfireConvergeWindows", crossfire);
        Assert.DoesNotContain("ClearSwarmCrossfireState(", crossfire);
        Assert.Contains("SwarmCrossfireState crossfire = GetSwarmMatchRuntime(matchingId).Crossfire;", crossfire);
        Assert.Contains("public SwarmCrossfireState Crossfire { get; }", runtimeStates);

        Assert.Contains("_swarmMatchRuntimes.TryGet(", botDodge);
        Assert.Contains("runtime.Crossfire.DodgeSnapshot", botDodge);
        Assert.DoesNotContain("GetSwarmMatchRuntime(", botDodge);
        Assert.DoesNotContain("GetOrCreate(", botDodge);
    }

    [Fact]
    public void MatchExecutionBoundary_UsesMatchOwnedRandomAndThreadSafeCachesWithoutGlobalLock()
    {
        string root = FindRepositoryRoot();
        string registry = ReadNormalizedSource(
            root, "game_server", "Services", "MatchRuntimeRegistry.cs");
        string runtimeStates = ReadNormalizedSource(
            root, "game_server", "Services", "SwarmArenaStates.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        string crossfire = ReadNormalizedSource(root, "game_server", "GameServer.SwarmCrossfire.cs");

        Assert.DoesNotContain("_globalExecutionLock", registry);
        Assert.Equal(5, CountOccurrences(registry, "lock (runtime.SyncRoot)"));

        Assert.DoesNotContain("_swarmCriticalRng", arena);
        Assert.DoesNotContain("_swarmCriticalRng", crossfire);
        Assert.Contains("private readonly Random _criticalRng = new();", runtimeStates);
        Assert.Contains(".Pacing.RollCritical(", arena);
        Assert.Contains("runtime.Pacing.RollCritical(", crossfire);

        Assert.Contains(
            "Lazy<IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>>",
            arena);
        Assert.Contains(
            "Lazy<IReadOnlyList<ClosureWaveDefinition>> _swarmFieldDerivedWaves",
            arena);
        Assert.Equal(
            2,
            CountOccurrences(arena, "LazyThreadSafetyMode.ExecutionAndPublication"));
        Assert.DoesNotContain("_swarmAreaCellsByDistance == null", arena);
        Assert.DoesNotContain("_swarmFieldDerivedWaves ??=", arena);
    }

    [Fact]
    public void SwarmAttackEventState_IsOwnedBySwarmMatchRuntimeAndRemainsDormant()
    {
        string root = FindRepositoryRoot();
        string attackEvents = ReadNormalizedSource(root, "game_server", "GameServer.SwarmAttackEvents.cs");
        string arena = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");

        Assert.DoesNotContain("_nextSwarmAttackEventId", attackEvents);
        Assert.DoesNotContain("_swarmActiveAttackEvents", attackEvents);
        Assert.DoesNotContain("_swarmAttackNextReadyAtUtc", attackEvents);
        Assert.DoesNotContain("_swarmAttackNextAttributeAtUtc", attackEvents);
        Assert.DoesNotContain("_swarmAttackCurrentTargets", attackEvents);
        Assert.DoesNotContain("_pendingSwarmAttackVisuals", attackEvents);
        Assert.DoesNotContain("_pendingSwarmAttackHits", attackEvents);
        Assert.DoesNotContain("CleanupSwarmPvpAttackEvents(", attackEvents);
        Assert.Contains("GetSwarmMatchRuntime(matchingId).AttackEvents", attackEvents);
        Assert.Contains("private static readonly bool SwarmPvpRangedAttackEnabled = false;", arena);
        Assert.Contains(
            "if (SwarmPvpRangedAttackEnabled)\n        {\n            ProcessSwarmPvpAttackEvents(",
            ReadSwarmArenaTick());
    }

    private static string ReadSwarmArenaTick()
    {
        string root = FindRepositoryRoot();
        string source = ReadNormalizedSource(root, "game_server", "GameServer.SwarmArena.cs");
        return ReadMethodSlice(
            source,
            "private void ProcessSwarmArenaForMatching(",
            "private double GetSwarmSafeDistance(");
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        string? previousMarker = null;

        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(
                marker,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(
                currentIndex > previousIndex,
                $"Expected '{marker}' after '{previousMarker ?? "method start"}'.");
            previousIndex = currentIndex;
            previousMarker = marker;
        }
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int searchStart = 0;

        while (true)
        {
            int index = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return count;

            count++;
            searchStart = index + marker.Length;
        }
    }

    private static string ReadMethodSlice(
        string source,
        string startMarker,
        string endMarker)
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
        return File.ReadAllText(Path.Combine(fullPathParts))
            .Replace("\r\n", "\n");
    }
}
