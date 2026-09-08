using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchEnvironmentalTickTests
{
    private static readonly DateTime StartedAt = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnregisteredAndCountdownMatches_DoNotStartEnvironmentalClock()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(101);
        lock (match.MatchLock)
        {
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt, null, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(-1), StartedAt, match.IsEnded));
            Assert.Null(match.TickSchedule.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void FirstSettlement_IsFiveSecondsAfterGameplayStarts()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(102);
        lock (match.MatchLock)
        {
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(4999), StartedAt, match.IsEnded));
            Assert.True(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, match.IsEnded));
            Assert.Equal(StartedAt.AddSeconds(10), match.TickSchedule.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void RepeatedPulses_DoNotApplyTheSameIntervalTwice()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(103);
        lock (match.MatchLock)
        {
            Assert.True(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, match.IsEnded));
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(5050), StartedAt, match.IsEnded));
            Assert.True(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(10), StartedAt, match.IsEnded));
        }
    }

    [Fact]
    public void DelayedPulse_AppliesOnceAndKeepsTheMatchStartBoundaries()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(104);
        lock (match.MatchLock)
        {
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt, match.IsEnded));
            // 잠금 경합 등으로 중간 펄스를 놓쳤더라도 밀린 다섯 번을 몰아서 적용하지 않는다.
            Assert.True(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(26), StartedAt, match.IsEnded));
            Assert.Equal(StartedAt.AddSeconds(30), match.TickSchedule.NextEnvironmentalTickAtUtc);
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(26050), StartedAt, match.IsEnded));
            Assert.True(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(30), StartedAt, match.IsEnded));
        }
    }

    [Fact]
    public void DifferentMatches_HaveIndependentDeadlines()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(105);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(106);
        lock (first.MatchLock)
            Assert.True(first.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, first.IsEnded));
        lock (second.MatchLock)
        {
            Assert.False(second.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt.AddSeconds(3), second.IsEnded));
            Assert.Equal(StartedAt.AddSeconds(8), second.TickSchedule.NextEnvironmentalTickAtUtc);
        }
        Assert.Equal(StartedAt.AddSeconds(10), first.TickSchedule.NextEnvironmentalTickAtUtc);
    }

    [Fact]
    public void TerminalMatch_DoesNotAdvanceDeadlineOrSettleAgain()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(107);
        lock (match.MatchLock)
        {
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt, StartedAt, match.IsEnded));
            Assert.True(match.TryMarkEnded());
            Assert.False(match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, match.IsEnded));
            Assert.Equal(StartedAt.AddSeconds(5), match.TickSchedule.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void SchedulingRequiresTheMatchLock()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(108);
        Assert.Throws<InvalidOperationException>(() =>
            match.TickSchedule.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt, match.IsEnded));
    }
}
