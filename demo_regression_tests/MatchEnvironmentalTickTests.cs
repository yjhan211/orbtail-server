using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchEnvironmentalTickTests
{
    private static readonly DateTime StartedAt = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnregisteredAndCountdownMatches_DoNotStartEnvironmentalClock()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(101);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt, null));
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(-1), StartedAt));
            Assert.Equal(0, matchSchedule.LastEnvironmentInterval);
        }
    }

    [Fact]
    public void FirstSettlement_IsFiveSecondsAfterGameplayStarts()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(102);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(4999), StartedAt));
            Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.Equal(1, matchSchedule.LastEnvironmentInterval);
        }
    }

    [Fact]
    public void RepeatedPulses_DoNotApplyTheSameIntervalTwice()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(103);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(5050), StartedAt));
            Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(10), StartedAt));
        }
    }

    [Fact]
    public void DelayedPulse_AppliesOnceAndKeepsTheMatchStartBoundaries()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(104);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            // 잠금 경합 등으로 중간 펄스를 놓쳤더라도 밀린 다섯 번을 몰아서 적용하지 않는다.
            Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(26), StartedAt));
            Assert.Equal(5, matchSchedule.LastEnvironmentInterval);
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(26050), StartedAt));
            Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(30), StartedAt));
        }
    }

    [Fact]
    public void DifferentMatches_HaveIndependentIntervals()
    {
        var firstStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = firstStore.GetOrCreate(105);
        using var firstSchedule = new TestMatchTickServices.ScheduleProbe(first, firstStore);
        var secondStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var second = secondStore.GetOrCreate(106);
        using var secondSchedule = new TestMatchTickServices.ScheduleProbe(second, secondStore);
        lock (first.MatchLock)
            Assert.True(firstSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
        lock (second.MatchLock)
        {
            Assert.False(secondSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt.AddSeconds(3)));
            Assert.Equal(0, secondSchedule.LastEnvironmentInterval);
        }
        Assert.Equal(1, firstSchedule.LastEnvironmentInterval);
    }

    [Fact]
    public void TerminalMatch_DoesNotAdvanceIntervalOrSettleAgain()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(107);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        lock (match.MatchLock)
        {
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            Assert.True(match.TryMarkEnded());
            Assert.False(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.Equal(0, matchSchedule.LastEnvironmentInterval);
        }
    }

    [Fact]
    public void SchedulingAcquiresTheMatchLock()
    {
        var matchStore = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = matchStore.GetOrCreate(108);
        using var matchSchedule = new TestMatchTickServices.ScheduleProbe(match, matchStore);
        Assert.True(matchSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
        Assert.False(Monitor.IsEntered(match.MatchLock));
    }
}
