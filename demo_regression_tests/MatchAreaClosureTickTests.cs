using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchAreaClosureTickTests
{
    private static readonly DateTime StartedAt = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequiresMatchLockAndDoesNotRunBeforeGameplay()
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(801);
        Assert.Throws<InvalidOperationException>(() => match.TryBeginAreaClosureTick(StartedAt, StartedAt));
        lock (match.Sync)
        {
            Assert.False(match.TryBeginAreaClosureTick(StartedAt, null));
            Assert.False(match.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(999), StartedAt));
            Assert.True(match.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
        }
    }

    [Fact]
    public void RunsOncePerSecondAndDoesNotReplayMissedIntervals()
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(802);
        lock (match.Sync)
        {
            Assert.True(match.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
            Assert.False(match.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(1050), StartedAt));
            Assert.True(match.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt));
            Assert.False(match.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt));
            Assert.Equal(StartedAt.AddSeconds(13), match.NextAreaClosureTickAtUtc);
            match.TryMarkTerminal();
            Assert.False(match.TryBeginAreaClosureTick(StartedAt.AddSeconds(20), StartedAt));
        }
    }

    [Fact]
    public void EachMatchHasItsOwnClosureSchedule()
    {
        var first = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(803);
        var second = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(804);
        lock (first.Sync)
            Assert.True(first.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
        Assert.Null(second.NextAreaClosureTickAtUtc);
        lock (second.Sync)
            Assert.True(second.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
    }
}
