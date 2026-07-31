using game_server.services;

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
                    new SurvivorFinalPlayerStats(10, 1, 120, 2, 400, 20),
                    new SurvivorFinalPlayerStats(-101, 2, 110, 1, 250, 0)
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
        Assert.False(Assert.Single(summary.Participants, player => player.PlayerId == 10).IsBot);
        var bot = Assert.Single(summary.Participants, player => player.PlayerId == -101);
        Assert.True(bot.IsBot);
        Assert.Equal(3, bot.SummonStonesEarned);
    }

    [Fact]
    public void Save_CountsBotOrbMergesAlongsidePlayerMerges()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new MatchSummaryFileStore(_directory, 5);
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = now, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = now + 1_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = 10, Outcome = "merge"
            },
            new()
            {
                Seq = 3, TimestampUnixMs = now + 2_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = -101, IsBot = true, Outcome = "bot_merge"
            },
            new()
            {
                Seq = 4, TimestampUnixMs = now + 3_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = -101, IsBot = true, Outcome = "bot_summon"
            }
        };

        var summary = store.Save(206003, "last_survivor", 10, events);

        Assert.Equal(1, Assert.Single(summary.Participants, player => player.PlayerId == 10).MergeCount);
        var bot = Assert.Single(summary.Participants, player => player.PlayerId == -101);
        Assert.Equal(1, bot.MergeCount);
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

        var recent = store.ListRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.DoesNotContain(recent, summary => summary.MatchingId == 1);
    }

    [Fact]
    public void FinalizationGate_AcceptsOnlyTheFirstCaller()
    {
        var events = new GameEventLogManager();

        Assert.True(events.TryBeginFinalization(206003));
        Assert.False(events.TryBeginFinalization(206003));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
