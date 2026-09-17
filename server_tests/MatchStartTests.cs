using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data.models;

namespace server_tests;

public sealed class MatchStartTests
{
    [Fact]
    public void AllHumansReady_SetsOneStartTime_AndClockControlsActivation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(98101);
        var state = match;
        Assert.Null(state.EntryDeadlineUtc);
        Assert.False(state.IsGameplayActive(DateTime.MaxValue));
        state.PrepareEntry(1, 2, -3);
        state.BeginEntry(2);
        state.MarkPlayerReady(1);
        state.MarkPlayerReady(1);
        state.MarkPlayerReady(999);
        Assert.Null(state.StartsAtUtc);
        Assert.NotNull(state.EntryDeadlineUtc);
        state.MarkPlayerReady(2);
        var startsAt = state.StartsAtUtc!.Value;
        Assert.InRange((startsAt - DateTime.UtcNow).TotalSeconds, 0, 5);
        state.MarkPlayerReady(2);
        Assert.Equal(startsAt, state.StartsAtUtc);
        Assert.False(state.IsGameplayActive(startsAt.AddTicks(-1)));
        Assert.True(state.IsGameplayActive(startsAt));
        Assert.False(state.IsEntryTimedOut(startsAt.AddMinutes(5)));
        using (match.Enter()) match.TryMarkEnded();
    }

    [Fact]
    public void MatchesAreIndependent_AndReusedIdGetsFreshState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(98102);
        var second = store.GetOrCreate(98103);
        first.StartGameplay();
        Assert.True(first.IsGameplayActive(DateTime.UtcNow));
        Assert.False(second.IsGameplayActive(DateTime.UtcNow));
        using (first.Enter()) first.TryMarkEnded();
        Assert.False(first.IsGameplayActive(DateTime.UtcNow));
        var replacement = store.GetOrCreate(98102);
        Assert.NotSame(first, replacement);
        Assert.Null(replacement.StartsAtUtc);
        Assert.False(replacement.IsGameplayActive(DateTime.UtcNow));
        using (replacement.Enter()) replacement.TryMarkEnded();
        using (second.Enter()) second.TryMarkEnded();
    }

    [Fact]
    public void EntryDeadlineDoesNotExtend_AndUnknownPlayerCannotStartMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(98104);
        var state = match;
        state.PrepareEntry(1, 2, -3);
        var deadline = state.EntryDeadlineUtc;
        state.BeginEntry(2);
        Assert.Equal(deadline, state.EntryDeadlineUtc);
        state.MarkPlayerReady(999);
        Assert.Null(state.StartsAtUtc);
        Assert.False(state.IsEntryTimedOut(DateTime.UtcNow));
        Assert.True(state.IsEntryTimedOut(DateTime.UtcNow.AddMinutes(1)));
        using (match.Enter()) match.TryMarkEnded();
    }
}
