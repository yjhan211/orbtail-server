using System.Text.RegularExpressions;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

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
            "private List<GameResultPlayerInfo> BuildGameResultPlayers(");

        int normalFinalEvent = Find(normalFinalization, "_gameEventLogManager.LogMatchEnded(");
        int normalCapture = Find(normalFinalization, "MatchSummaryPersistence.Capture(");
        int resultPacket = Find(normalFinalization, "GameResultPacketChunker.CreateGameResultChunks(");
        int markEnded = Find(normalFinalization, "session.MarkGameEnded()");
        const string persistCallPattern = @"MatchSummaryPersistence\.Persist\s*\(";
        RegexOptions sourceContractOptions =
            RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant;

        Assert.True(normalFinalEvent < normalCapture);
        Assert.True(normalCapture < resultPacket);
        Assert.True(resultPacket < markEnded);
        Assert.Single(Regex.Matches(normalFinalization, persistCallPattern));
        Assert.Matches(
            new Regex(
                """
                Action\?\s+afterFinalized\s*=\s*null\s*;\s*
                if\s*\(\s*summaryRequest\s*!=\s*null\s*\)\s*
                \{\s*
                    MatchSummaryPersistenceRequest\s+capturedSummary\s*=\s*summaryRequest\s*;\s*
                    afterFinalized\s*=\s*\(\s*\)\s*=>\s*
                        MatchSummaryPersistence\.Persist\s*
                        \(\s*capturedSummary\s*,\s*_matchSummaryFileStore\s*,\s*Logger\s*\)\s*;\s*
                \}\s*
                _cleanupMatchRuntime\s*
                \(\s*matchingId\s*,\s*afterFinalized\s*\)\s*;
                """,
                sourceContractOptions),
            normalFinalization);

        string serverSource = ReadNormalizedSource(root, "game_server", "GameServer.cs");
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
}
