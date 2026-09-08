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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(102);
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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(103);
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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(104);
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
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(105);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(106);
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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(107);
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
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(108);
        Assert.Throws<InvalidOperationException>(() =>
            match.TryBeginEnvironmentalTick(StartedAt.AddSeconds(5), StartedAt));
    }
}
