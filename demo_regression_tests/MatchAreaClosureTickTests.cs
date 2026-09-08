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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(801);
        Assert.Throws<InvalidOperationException>(() => match.TickSchedule.TryBeginAreaClosureTick(StartedAt, StartedAt, match.IsEnded));
        lock (match.MatchLock)
        {
            Assert.False(match.TickSchedule.TryBeginAreaClosureTick(StartedAt, null, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(999), StartedAt, match.IsEnded));
            Assert.True(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt, match.IsEnded));
        }
    }

    [Fact]
    public void RunsOncePerSecondAndDoesNotReplayMissedIntervals()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(802);
        lock (match.MatchLock)
        {
            Assert.True(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(1050), StartedAt, match.IsEnded));
            Assert.True(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt, match.IsEnded));
            Assert.Equal(StartedAt.AddSeconds(13), match.TickSchedule.NextAreaClosureTickAtUtc);
            match.TryMarkEnded();
            Assert.False(match.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(20), StartedAt, match.IsEnded));
        }
    }

    [Fact]
    public void EachMatchHasItsOwnClosureSchedule()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(803);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(804);
        lock (first.MatchLock)
            Assert.True(first.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt, first.IsEnded));
        Assert.Null(second.TickSchedule.NextAreaClosureTickAtUtc);
        lock (second.MatchLock)
            Assert.True(second.TickSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt, second.IsEnded));
    }
}
