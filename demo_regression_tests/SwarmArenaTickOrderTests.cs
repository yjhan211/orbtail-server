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
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);");

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
            "BroadcastOrbVisualStates(",
            "BroadcastSwarmOrbRankings(",
            "ProcessSwarmGrowthOffers(",
            "ProcessSwarmScoreTimeout(",
            "ProcessPendingSwarmMonsterHits(",
            "ProcessSwarmCrossfires(",
            "ApplySwarmPvpAttack(matchingId, pending.Attack",
            "_proximityAutoCombatResolver.Resolve(",
            "_botPlayerManager.TryFinalizeProximityAutoCombatElimination(");
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
    public void SwarmCleanup_AlwaysDropsMatchOwnedRuntimeAfterLegacyCleanup()
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
            "CleanupSwarmPvpAttackEvents(matchingId);",
            "finally",
            "_swarmMatchRuntimes.Remove(matchingId);");
        Assert.DoesNotContain("ClearSwarmWindBladeState", cleanupBody);
        Assert.DoesNotContain("ClearSwarmOrbBoardState", cleanupBody);
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
