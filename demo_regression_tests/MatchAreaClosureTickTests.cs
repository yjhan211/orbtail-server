using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchAreaClosureTickTests
{
    private static readonly DateTime StartedAt = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DoesNotRunBeforeGameplay()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(801);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.False(matchSchedule.TryBeginAreaClosureTick(StartedAt, null));
            Assert.False(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(999), StartedAt));
            Assert.True(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
        }
    }

    [Fact]
    public void RunsOncePerSecondAndDoesNotReplayMissedIntervals()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(802);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.True(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
            Assert.False(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddMilliseconds(1050), StartedAt));
            Assert.True(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt));
            Assert.False(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(12), StartedAt));
            Assert.Equal(12, matchSchedule.LastAreaClosureSecond);
            match.TryMarkEnded();
            Assert.False(matchSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(20), StartedAt));
        }
    }

    [Fact]
    public void DelayedTicksKeepOneSecondBoundariesFromGameplayStart()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(805);
        using var schedule = new TestMatchTickServices.ScheduleProbe(match, store);
        // 벽시계의 정각이 아니라 실제 매치 시작 시각을 기준으로 한다.
        var startedAt = StartedAt.AddMilliseconds(125);
        Assert.True(schedule.TryBeginAreaClosureTick(startedAt.AddMilliseconds(10300), startedAt));
        Assert.Equal(10, schedule.LastAreaClosureSecond);
        Assert.False(schedule.TryBeginAreaClosureTick(startedAt.AddMilliseconds(10300), startedAt));
        Assert.False(schedule.TryBeginAreaClosureTick(startedAt.AddMilliseconds(10999), startedAt));
        Assert.True(schedule.TryBeginAreaClosureTick(startedAt.AddSeconds(11), startedAt));
        Assert.Equal(11, schedule.LastAreaClosureSecond);

        Assert.True(schedule.TryBeginAreaClosureTick(startedAt.AddMilliseconds(26400), startedAt));
        Assert.Equal(26, schedule.LastAreaClosureSecond);
        Assert.False(schedule.TryBeginAreaClosureTick(startedAt.AddMilliseconds(26400), startedAt));
        Assert.True(schedule.TryBeginAreaClosureTick(startedAt.AddSeconds(27), startedAt));
    }

    [Fact]
    public void EachMatchHasItsOwnClosureSchedule()
    {
        var firstStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = firstStore.GetOrCreate(803);
        using var firstSchedule = new TestMatchTickServices.ScheduleProbe(first, firstStore);
        var secondStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var second = secondStore.GetOrCreate(804);
        using var secondSchedule = new TestMatchTickServices.ScheduleProbe(second, secondStore);
        lock (first.MatchLock)
            Assert.True(firstSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
        Assert.Equal(0, secondSchedule.LastAreaClosureSecond);
        lock (second.MatchLock)
            Assert.True(secondSchedule.TryBeginAreaClosureTick(StartedAt.AddSeconds(1), StartedAt));
    }
}
