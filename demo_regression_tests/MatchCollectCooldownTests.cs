using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class MatchCollectCooldownTests
{
    [Fact]
    public void SameMatchSharesCooldownButOtherMatchIsIndependent()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        int acquired = 0;
        Parallel.For(0, 32, attempt =>
        {
            if (first.CollectCooldowns.TryAcquireCooldown(10, 60, out _))
                Interlocked.Increment(ref acquired);
        });
        Assert.Equal(1, acquired);
        Assert.False(first.CollectCooldowns.TryAcquireCooldown(10, 60, out int remaining));
        Assert.InRange(remaining, 1, 60);
        Assert.True(second.CollectCooldowns.TryAcquireCooldown(10, 60, out _));
        Assert.Single(first.CollectCooldowns.GetSnapshot());
        Assert.Single(second.CollectCooldowns.GetSnapshot());
    }

    [Fact]
    public void ClearAndExpirationAllowTheSpotToBeAcquiredAgain()
    {
        var cooldowns = new RngCollectCooldownStore();
        Assert.True(cooldowns.TryAcquireCooldown(10, 60, out _));
        cooldowns.ClearCooldown(10);
        Assert.Empty(cooldowns.GetSnapshot());
        Assert.True(cooldowns.TryAcquireCooldown(10, 0, out _));
        Assert.Empty(cooldowns.GetSnapshot());
        Assert.True(cooldowns.TryAcquireCooldown(10, 60, out _));
    }

    [Fact]
    public void MatchEndClearsStateAndRejectsLateAcquisition()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        first.CollectCooldowns.TryAcquireCooldown(10, 60, out _);
        second.CollectCooldowns.TryAcquireCooldown(10, 60, out _);
        using (store.Enter(first)) first.TryMarkTerminal();
        Assert.Null(store.Get(1));
        Assert.Empty(first.CollectCooldowns.GetSnapshot());
        Assert.False(first.CollectCooldowns.TryAcquireCooldown(10, 60, out _));
        Assert.Single(second.CollectCooldowns.GetSnapshot());
    }
}
