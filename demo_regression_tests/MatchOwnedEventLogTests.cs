using game_server.matches.logging;
using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchOwnedEventLogTests
{
    [Fact]
    public void ActiveStateBelongsToMatchAndIsReleasedDuringCleanup()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        using (MatchRuntimeStore.Enter(first))
        {
            logs.BeginMatch(1, 100);
            logs.RecordRecovery(1, 10, 5);
            Assert.True(logs.TryBeginFinalization(1));
        }
        using (MatchRuntimeStore.Enter(second))
        {
            logs.BeginMatch(2, 200);
            logs.RecordRecovery(2, 20, 7);
        }
        Assert.NotSame(first.EventLog.Log, second.EventLog.Log);
        Assert.NotSame(first.EventLog.Combat, second.EventLog.Combat);
        Assert.NotSame(first.EventLog.Telemetry, second.EventLog.Telemetry);
        Assert.NotEmpty(logs.GetForPersistence(1));
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.Null(store.GetOrNull(1));
        Assert.Null(first.EventLog.Log);
        Assert.Null(first.EventLog.Combat);
        Assert.Null(first.EventLog.Telemetry);
        Assert.Empty(logs.GetRecent(1));
        Assert.Empty(logs.GetForPersistence(1));
        Assert.False(logs.TryBeginFinalization(1));
        Assert.Throws<InvalidOperationException>(() => logs.LogSystem(1, "late"));
        Assert.NotEmpty(logs.GetRecent(2));
        using (MatchRuntimeStore.Enter(second)) Assert.True(logs.TryBeginFinalization(2));
    }

    [Fact]
    public void FinishedMatchesDoNotRetainQueryableLogs()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        for (long id = 1; id <= 51; id++)
        {
            var match = store.GetOrCreate(id);
            using (MatchRuntimeStore.Enter(match))
            {
                logs.LogSystem(id, "event");
                match.TryMarkEnded();
            }
        }
        Assert.Equal(0, store.Count);
        Assert.Empty(logs.GetRecent(1));
        Assert.Empty(logs.GetRecent(2));
        Assert.Empty(logs.GetRecent(51));
    }
}
