using game_server.logging;
using game_server.matches.results;
using game_server.matches;

namespace demo_regression_tests;

public sealed class MatchSummaryFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"manitto-match-summary-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_CanBeReadByANewStoreInstance()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new MatchSummaryFileStore(_directory, 5);
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = now, Type = "MATCH_STARTED", MatchSeed = 7 },
            new()
            {
                Seq = 2,
                TimestampUnixMs = now + 1_000,
                Type = "SUMMON_STONE_AWARDED",
                PlayerId = -101,
                IsBot = true,
                SummonStoneDelta = 3
            },
            new()
            {
                Seq = 3,
                TimestampUnixMs = now + 2_000,
                Type = "MATCH_ENDED",
                WinnerPlayerId = 10,
                EndReason = "last_survivor",
                FinalPlayerStats =
                [
                    new MatchFinalPlayerStats(10, 1, 120, 2, 400, 20),
                    new MatchFinalPlayerStats(-101, 2, 110, 1, 250, 0)
                ]
            }
        };

        store.Save(206001, "last_survivor", 10, events);
        var restartedStore = new MatchSummaryFileStore(_directory, 5);
        var summary = restartedStore.Read(206001);

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
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = store.Save(206002, "last_survivor", 1,
            [new GameEventEntry { Seq = 1, TimestampUnixMs = now, Type = "MATCH_ENDED" }]);
        var duplicate = store.Save(206002, "last_human_left", 0,
            [new GameEventEntry { Seq = 2, TimestampUnixMs = now + 1_000, Type = "MATCH_ABANDONED" }]);

        Assert.Equal(first.EndReason, duplicate.EndReason);
        Assert.Equal("last_survivor", store.Read(206002)!.EndReason);
    }

    [Fact]
    public void Save_PrunesOldSummariesToTheConfiguredLimit()
    {
        var store = new MatchSummaryFileStore(_directory, 2);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (long matchingId = 1; matchingId <= 3; matchingId++)
        {
            store.Save(matchingId, "test", 0,
                [new GameEventEntry { Seq = matchingId, TimestampUnixMs = now + matchingId, Type = "MATCH_ENDED" }]);
            Thread.Sleep(20);
        }

        var recent = Directory.GetFiles(_directory, "match-*.json");
        Assert.Equal(2, recent.Length);
        Assert.Null(store.Read(1));
        Assert.False(File.Exists(Path.Combine(_directory, "match-1.events.jsonl")));
        Assert.NotNull(store.Read(3));
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
        Assert.DoesNotContain(recent, entry => entry.Type == "MATCH_STARTED");
        Assert.Equal(5_101, complete.Count);
        Assert.Equal("MATCH_STARTED", complete[0].Type);

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 0, complete);
        var persisted = store.ReadRawEvents(matchingId, 6_000);

        Assert.Equal(5_101, summary.RawEventCount);
        Assert.Equal(500, summary.Events.Count);
        Assert.Equal(5_101, persisted.Count);
        Assert.Contains(persisted, entry => entry.Type == "MATCH_STARTED");
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
        Assert.Equal("AFTERIMAGE_KILLED", killed.Type);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
