using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchEnvironmentalTickTests
{
    private static readonly DateTime StartedAt = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnregisteredAndCountdownMatches_DoNotStartEnvironmentalClock()
    {
        var match = new MatchRuntime(101, NullLogger.Instance);
        lock (match.Sync)
        {
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt, null));
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(-1), StartedAt));
            Assert.Null(match.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void FirstSettlement_IsFiveSecondsAfterGameplayStarts()
    {
        var match = new MatchRuntime(102, NullLogger.Instance);
        lock (match.Sync)
        {
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(4999), StartedAt));
            Assert.True(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.Equal(StartedAt.AddSeconds(10), match.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void RepeatedPulses_DoNotApplyTheSameIntervalTwice()
    {
        var match = new MatchRuntime(103, NullLogger.Instance);
        lock (match.Sync)
        {
            Assert.True(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(5050), StartedAt));
            Assert.True(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(10), StartedAt));
        }
    }

    [Fact]
    public void DelayedPulse_AppliesOnceAndKeepsTheMatchStartBoundaries()
    {
        var match = new MatchRuntime(104, NullLogger.Instance);
        lock (match.Sync)
        {
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            // 잠금 경합 등으로 중간 펄스를 놓쳤더라도 밀린 다섯 번을 몰아서 적용하지 않는다.
            Assert.True(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(26), StartedAt));
            Assert.Equal(StartedAt.AddSeconds(30), match.NextEnvironmentalTickAtUtc);
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddMilliseconds(26050), StartedAt));
            Assert.True(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(30), StartedAt));
        }
    }

    [Fact]
    public void DifferentMatches_HaveIndependentDeadlines()
    {
        var first = new MatchRuntime(105, NullLogger.Instance);
        var second = new MatchRuntime(106, NullLogger.Instance);
        lock (first.Sync)
            Assert.True(first.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
        lock (second.Sync)
        {
            Assert.False(second.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt.AddSeconds(3)));
            Assert.Equal(StartedAt.AddSeconds(8), second.NextEnvironmentalTickAtUtc);
        }
        Assert.Equal(StartedAt.AddSeconds(10), first.NextEnvironmentalTickAtUtc);
    }

    [Fact]
    public void TerminalMatch_DoesNotAdvanceDeadlineOrSettleAgain()
    {
        var match = new MatchRuntime(107, NullLogger.Instance);
        lock (match.Sync)
        {
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt, StartedAt));
            Assert.True(match.TryMarkTerminal());
            Assert.False(match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
            Assert.Equal(StartedAt.AddSeconds(5), match.NextEnvironmentalTickAtUtc);
        }
    }

    [Fact]
    public void SchedulingRequiresTheMatchLock()
    {
        var match = new MatchRuntime(108, NullLogger.Instance);
        Assert.Throws<InvalidOperationException>(() =>
            match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
    }
}
