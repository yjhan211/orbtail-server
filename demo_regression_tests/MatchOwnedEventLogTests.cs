using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchOwnedEventLogTests
{
    [Fact]
    public void ActiveStateBelongsToMatchAndOnlyArchivedEventsSurviveCleanup()
    {
        var archive = new MatchEventArchive();
        var store = new MatchRuntimeStore(NullLogger.Instance, eventArchive: archive);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog, archive);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        using (store.Enter(first))
        {
            logs.BeginMatch(1, 100);
            logs.RecordRecovery(1, 10, 5);
            Assert.True(logs.TryBeginFinalization(1));
        }
        using (store.Enter(second))
        {
            logs.BeginMatch(2, 200);
            logs.RecordRecovery(2, 20, 7);
        }
        Assert.NotSame(first.EventLog.Log, second.EventLog.Log);
        Assert.NotSame(first.EventLog.Combat, second.EventLog.Combat);
        Assert.NotSame(first.EventLog.Telemetry, second.EventLog.Telemetry);
        var events = logs.GetForPersistence(1);
        using (store.Enter(first)) first.TryMarkTerminal();
        Assert.Null(store.Get(1));
        Assert.Null(first.EventLog.Log);
        Assert.Null(first.EventLog.Combat);
        Assert.Null(first.EventLog.Telemetry);
        Assert.Equal(events.Select(e => e.Seq), logs.GetRecent(1).Select(e => e.Seq));
        Assert.False(logs.TryBeginFinalization(1));
        Assert.Throws<InvalidOperationException>(() => logs.LogSystem(1, "late"));
        Assert.NotEmpty(logs.GetRecent(2));
        using (store.Enter(second)) Assert.True(logs.TryBeginFinalization(2));
    }

    [Fact]
    public void ArchiveKeepsOnlyLatestFiftyMatches()
    {
        var archive = new MatchEventArchive();
        var store = new MatchRuntimeStore(NullLogger.Instance, eventArchive: archive);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog, archive);
        for (long id = 1; id <= 51; id++)
        {
            var match = store.GetOrCreate(id);
            using (store.Enter(match))
            {
                logs.LogSystem(id, "event");
                match.TryMarkTerminal();
            }
        }
        Assert.Equal(0, store.Count);
        Assert.Empty(logs.GetRecent(1));
        Assert.Single(logs.GetRecent(2));
        Assert.Single(logs.GetRecent(51));
    }
}
