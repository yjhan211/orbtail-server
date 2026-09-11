using game_server.matches.logging;
using game_server.matches.results;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchSummaryFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"manitto-match-summary-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_WritesReadableSummaryFile()
    {
        var store = new MatchSummaryFileStore(_directory, 5);
        var logs = TestGameEventLogs.Create();
        logs.BeginMatch(206001, 7);
        logs.LogSystem(206001, "test event");
        logs.LogMatchEnded(206001, 10, "last_survivor", "none",
        [
            new MatchFinalPlayerStats(10, 1, 120, 2, 400, 20),
            new MatchFinalPlayerStats(-101, 2, 110, 1, 250, 0)
        ]);
        var document = MatchSummaryFileStore.Prepare(logs, NullLogger.Instance,
            206001, "last_survivor", 10, out var capturedEvents);
        Assert.NotNull(document);
        store.Save(document, capturedEvents, NullLogger.Instance);
        var summary = ReadSummary(206001);

        Assert.NotNull(summary);
        Assert.Equal("last_survivor", summary!.EndReason);
        Assert.Equal(10, summary.WinnerPlayerId);
        Assert.Equal(3, summary.Events.Count);
        Assert.Equal(1, Assert.Single(summary.Participants, player => player.PlayerId == 10).Rank);
        var bot = Assert.Single(summary.Participants, player => player.PlayerId == -101);
        Assert.Equal(2, bot.Rank);
        Assert.Equal(110, bot.SurvivalTimeSeconds);
        Assert.Equal(250, bot.TotalDamageDealt);
    }

    [Fact]
    public void Save_IsIdempotentForTheSameMatchingId()
    {
        var store = new MatchSummaryFileStore(_directory, 5);
        var logs = TestGameEventLogs.Create();
        logs.BeginMatch(206002, 7);
        var first = MatchSummaryFileStore.Prepare(logs, NullLogger.Instance,
            206002, "last_survivor", 1, out var firstEvents);
        Assert.NotNull(first);
        store.Save(first, firstEvents, NullLogger.Instance);

        logs.LogSystem(206002, "must not overwrite the first save");
        var duplicate = MatchSummaryFileStore.Prepare(logs, NullLogger.Instance,
            206002, "last_human_left", 0, out var duplicateEvents);
        Assert.NotNull(duplicate);
        store.Save(duplicate, duplicateEvents, NullLogger.Instance);
        Assert.Single(store.ReadRawEvents(206002));
        Assert.Equal("last_survivor", ReadSummary(206002)!.EndReason);
    }

    [Fact]
    public void Save_PrunesOldSummariesToTheConfiguredLimit()
    {
        var store = new MatchSummaryFileStore(_directory, 2);
        var logs = TestGameEventLogs.Create();

        for (long matchingId = 1; matchingId <= 3; matchingId++)
        {
            logs.BeginMatch(matchingId, 7);
            var document = MatchSummaryFileStore.Prepare(logs, NullLogger.Instance,
                matchingId, "test", 0, out var capturedEvents);
            Assert.NotNull(document);
            store.Save(document, capturedEvents, NullLogger.Instance);
            Thread.Sleep(20);
        }

        var recent = Directory.GetFiles(_directory, "match-*.json");
        Assert.Equal(2, recent.Length);
        Assert.Null(ReadSummary(1));
        Assert.False(File.Exists(Path.Combine(_directory, "match-1.events.jsonl")));
        Assert.NotNull(ReadSummary(3));
    }

    [Fact]
    public void FullPersistenceKeepsEarlyEventsBeyondTheLiveFiveThousandEventWindow()
    {
        const long matchingId = 210001;
        var log = TestGameEventLogs.Create();
        log.BeginMatch(matchingId, 210);
        for (int index = 0; index < 5_100; index++)
            log.LogSystem(matchingId, $"event-{index}");

        var recent = log.GetRecent(matchingId);
        var complete = log.GetForPersistence(matchingId);

        Assert.Equal(5_000, recent.Count);
        Assert.DoesNotContain(recent, entry => entry.Type == GameEventType.MatchStarted);
        Assert.Equal(5_101, complete.Count);
        Assert.Equal(GameEventType.MatchStarted, complete[0].Type);

        var store = new MatchSummaryFileStore(_directory, 5);
        var document = MatchSummaryFileStore.Prepare(log, NullLogger.Instance,
            matchingId, "test", 0, out var capturedEvents);
        Assert.NotNull(document);
        store.Save(document, capturedEvents, NullLogger.Instance);
        var summary = ReadSummary(matchingId);
        Assert.NotNull(summary);
        var persisted = store.ReadRawEvents(matchingId, 6_000);

        Assert.Equal(5_101, summary.RawEventCount);
        Assert.Equal(500, summary.Events.Count);
        Assert.Equal(5_101, persisted.Count);
        Assert.Contains(persisted, entry => entry.Type == GameEventType.MatchStarted);
    }

    [Fact]
    public void CoreAfterimageKillRecordsFirstLastAndDamageContributors()
    {
        var log = TestGameEventLogs.Create();

        log.LogSwarmAfterimageKilled(
            210003,
            202101,
            "S2Library1",
            isCore: true,
            firstAttackerPlayerId: 11,
            lastAttackerPlayerId: 22,
            new Dictionary<long, int> { [11] = 30, [22] = 18 });

        var killed = Assert.Single(log.GetRecent(210003));
        Assert.Equal(GameEventType.AfterimageKilled, killed.Type);
        Assert.Equal(11, killed.FirstAttackerPlayerId);
        Assert.Equal(22, killed.LastAttackerPlayerId);
        Assert.Equal([11L, 22L], killed.MonsterDamageContributions!.Select(entry => entry.PlayerId));
        Assert.Equal([30, 18], killed.MonsterDamageContributions!.Select(entry => entry.Damage));
    }

    [Fact]
    public void FinalizationGate_AcceptsOnlyTheFirstCaller()
    {
        var events = TestGameEventLogs.Create();

        Assert.True(events.TryBeginFinalization(206003));
        Assert.False(events.TryBeginFinalization(206003));
    }

    [Theory]
    [InlineData(GameEventType.MatchStarted, "MATCH_STARTED")]
    [InlineData(GameEventType.MatchEnded, "MATCH_ENDED")]
    [InlineData(GameEventType.SurvivorFirstT2, "SURVIVOR_FIRST_T2")]
    [InlineData(GameEventType.GroundItemPickedUp, "GROUND_ITEM_PICKED_UP")]
    public void EventType_PreservesExistingJsonStrings(GameEventType type, string storedName)
    {
        var entry = new GameEventEntry { Type = type };
        string json = System.Text.Json.JsonSerializer.Serialize(entry);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(storedName, document.RootElement.GetProperty("Type").GetString());
        var restored = System.Text.Json.JsonSerializer.Deserialize<GameEventEntry>(
            "{\"Type\":\"" + storedName + "\"}");
        Assert.NotNull(restored);
        Assert.Equal(type, restored.Type);
    }

    [Fact]
    public void EveryEventType_RoundTripsAsAString()
    {
        foreach (var type in Enum.GetValues<GameEventType>())
        {
            string json = System.Text.Json.JsonSerializer.Serialize(type);
            Assert.StartsWith("\"", json);
            Assert.Equal(type, System.Text.Json.JsonSerializer.Deserialize<GameEventType>(json));
        }
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<GameEventType>("123"));
    }
    private MatchSummaryDocument? ReadSummary(long matchingId)
    {
        string path = Path.Combine(_directory, $"match-{matchingId}.json");
        if (!File.Exists(path))
        {
            return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<MatchSummaryDocument>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
