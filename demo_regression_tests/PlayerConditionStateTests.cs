using game_server.services;
using network.common;
namespace demo_regression_tests;

public sealed class PlayerConditionStateTests
{
    [Fact]
    public void ResourceDeficitConvertsWithoutSharingPlayers()
    {
        var state = new PlayerConditionState { Stamina = 3 };
        Assert.Equal(4, state.ChangeResources(-5, 1, 100, 100));
        Assert.Equal(0, state.Stamina);
        Assert.Equal(5, state.Corruption);
        Assert.Equal(100, new PlayerConditionState().Stamina);
    }

    [Fact]
    public void SleepWaitsForWarmupAndDoesNotReplayBlockedTicks()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new PlayerConditionState { IsSleeping = true, Corruption = 80 };
        Assert.Equal(0, state.GetSleepRecovery(start, false, 100));
        Assert.Equal(0, state.GetSleepRecovery(start.AddMilliseconds(999), false, 100));
        Assert.Equal(5, state.GetSleepRecovery(start.AddSeconds(1), false, 100));
        Assert.Equal(0, state.GetSleepRecovery(start.AddSeconds(1), false, 100));
        state.HealLockUntilUtc = start.AddSeconds(4);
        Assert.Equal(0, state.GetSleepRecovery(start.AddSeconds(3), false, 100));
        Assert.Equal(5, state.GetSleepRecovery(start.AddSeconds(4), false, 100));
        state.LastCombatAtUtc = start.AddSeconds(4);
        Assert.False(state.CanSleep(start.AddSeconds(6)));
        Assert.True(state.CanSleep(start.AddSeconds(7)));
    }

    [Fact]
    public void PeriodicBuffReplacementAndExpiryKeepOneEffect()
    {
        var state = new PlayerConditionState { Stamina = 0 };
        state.AddPeriodicBuff(BuffSubType.CONDITION_ADD, 10, 1, 2);
        state.AddPeriodicBuff(BuffSubType.CONDITION_ADD, 3, 1, 2);
        void Apply(int stamina, int corruption) => state.ChangeResources(stamina, corruption, 100, 100);
        state.TickPeriodicBuffs(100, 100, Apply);
        Assert.Equal(3, state.Stamina);
        state.TickPeriodicBuffs(100, 100, Apply);
        Assert.Equal(6, state.Stamina);
        Assert.False(state.HasPeriodicBuffs);
    }
}
