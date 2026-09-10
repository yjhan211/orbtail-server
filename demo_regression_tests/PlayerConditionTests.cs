using network.common.data.models;
using game_server.matches;
using game_server.bots;
using game_server.items;
using game_server.players;
using game_server.sessions;
using network.common;
namespace demo_regression_tests;

public sealed class PlayerConditionTests
{
    [Fact]
    public void RecoveryResultDistinguishesRequestedAndActualAmount()
    {
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = Config.MAX_HEALTH - 3 };
        var change = condition.Recover(10);
        Assert.Equal(Config.MAX_HEALTH - 3, change.Before);
        Assert.Equal(Config.MAX_HEALTH, change.After);
        Assert.Equal(10, change.RequestedDelta);
        Assert.Equal(3, change.ActualDelta);
        Assert.Equal(3, change.Recovered);
        Assert.True(change.Changed);
        Assert.False(condition.Recover(10).Changed);
    }

    [Theory]
    [InlineData(PlayerState.IDLE)]
    [InlineData(PlayerState.EXPLORE_1)]
    public void LeavingSleepResetsRecoveryButPreservesPeriodicBuffs(PlayerState nextState)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 50 };
        condition.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 2, 1, 10);
        Assert.True(condition.TryStartSleep(now));
        Assert.Equal(PlayerState.SLEEP, condition.State);
        condition.GetSleepRecovery(now, false, 100);
        Assert.Equal(5, condition.GetSleepRecovery(now.AddSeconds(1), false, 100));

        condition.State = nextState;
        Assert.False(condition.IsSleeping);
        Assert.Equal(DateTime.MinValue, condition.SleepStartedAtUtc);
        Assert.True(condition.HasPeriodicBuffs);
        Assert.Equal(0, condition.GetSleepRecovery(now.AddSeconds(10), false, 100));
        condition.TickPeriodicBuffs(100, amount => condition.Recover(amount));
        Assert.Equal(52, condition.Health);

        Assert.True(condition.TryStartSleep(now.AddSeconds(10)));
        Assert.Equal(0, condition.GetSleepRecovery(now.AddSeconds(10), false, 100));
        Assert.Equal(5, condition.GetSleepRecovery(now.AddSeconds(11), false, 100));
    }

    [Fact]
    public void DamageResultClampsAtZeroAndDoesNotOverflow()
    {
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 7 };
        var change = condition.ApplyDamage(int.MaxValue);
        Assert.Equal(0, change.After);
        Assert.Equal(-7, change.ActualDelta);
        Assert.Equal(0, change.Recovered);
        Assert.True(change.IsDepleted);
        Assert.False(condition.ApplyDamage(1).Changed);
        Assert.Equal(Config.MAX_HEALTH, condition.Recover(int.MaxValue).After);
        Assert.Equal(Config.MAX_HEALTH, condition.Recover(int.MaxValue).After);
        Assert.Throws<ArgumentOutOfRangeException>(() => condition.ApplyDamage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => condition.Recover(-1));
    }

    [Fact]
    public void StartSleepChecksCombatAndHealingLocksAndDoesNotRestartSleep()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 50, LastCombatAtUtc = now };
        Assert.False(condition.TryStartSleep(now.AddSeconds(2)));
        condition.HealLockUntilUtc = now.AddSeconds(5);
        Assert.False(condition.TryStartSleep(now.AddSeconds(4)));
        Assert.True(condition.TryStartSleep(now.AddSeconds(5)));
        Assert.True(condition.IsSleeping);
        Assert.Equal(0, condition.GetSleepRecovery(now.AddSeconds(5), false, Config.MAX_HEALTH));
        Assert.False(condition.TryStartSleep(now.AddSeconds(6)));
        Assert.True(condition.GetSleepRecovery(now.AddSeconds(6), false, Config.MAX_HEALTH) > 0);
    }

    [Fact]
    public void MatchTicksPreservePeriodicBuffScheduleAndExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 20 };
        condition.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 3, 2, 4, now);
        void Apply(int amount) => condition.ChangeHealth(amount, 100);

        condition.UpdatePeriodicBuffs(now.AddMilliseconds(999), 100, Apply);
        Assert.Equal(20, condition.Health);
        condition.UpdatePeriodicBuffs(now.AddSeconds(1), 100, Apply);
        Assert.Equal(20, condition.Health);
        condition.UpdatePeriodicBuffs(now.AddSeconds(2), 100, Apply);
        Assert.Equal(23, condition.Health);
        condition.UpdatePeriodicBuffs(now.AddSeconds(2), 100, Apply);
        Assert.Equal(23, condition.Health);
        condition.UpdatePeriodicBuffs(now.AddSeconds(4), 100, Apply);
        Assert.Equal(26, condition.Health);
        Assert.False(condition.HasPeriodicBuffs);
        condition.UpdatePeriodicBuffs(now.AddSeconds(9), 100, Apply);
        Assert.Equal(26, condition.Health);
    }

    [Fact]
    public void ClearingBuffsResetsScheduleAndCancellationStopsCatchUp()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 20 };
        condition.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 3, 1, 10, now);
        condition.ClearPeriodicBuffs();
        condition.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 3, 1, 10, now.AddSeconds(10));
        int applied = 0;
        void Apply(int amount)
        {
            applied++;
            condition.ClearPeriodicBuffs();
        }
        condition.UpdatePeriodicBuffs(now.AddSeconds(10.9), 100, Apply);
        Assert.Equal(0, applied);
        condition.UpdatePeriodicBuffs(now.AddSeconds(20), 100, Apply);
        Assert.Equal(1, applied);
        Assert.False(condition.HasPeriodicBuffs);
    }

    [Fact]
    public void HeartPickupRequiresMissingHealth()
    {
        Assert.Equal(GroundItemPickupDisposition.LeaveOnGround,
            GroundItemPickupPolicy.Resolve(GroundItemPickupPolicy.HeartItemId,
                Config.MAX_HEALTH, out _));
        Assert.Equal(GroundItemPickupDisposition.AutoUse,
            GroundItemPickupPolicy.Resolve(GroundItemPickupPolicy.HeartItemId,
                Config.MAX_HEALTH - 1, out int recovery));
        Assert.Equal(GroundItemPickupPolicy.HeartRecovery, recovery);
    }

    [Fact]
    public void HealthPacketPreservesRemainingHealthAndSignedDelta()
    {
        var sent = new network.common.data.models.G_TO_C_PLAYER_STATS_UPDATE
        {
            Health = 70, HealthDelta = -30
        };
        byte[] bytes = MessagePack.MessagePackSerializer.Serialize(sent);
        var received = MessagePack.MessagePackSerializer.Deserialize<network.common.data.models.G_TO_C_PLAYER_STATS_UPDATE>(bytes);
        Assert.Equal(70, received.Health);
        Assert.Equal(-30, received.HealthDelta);
    }

    [Fact]
    public void PlayersAndBotsStartAtFullHealth()
    {
        Assert.Equal(Config.MAX_HEALTH, new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } }.Health);
        Assert.Equal(Config.MAX_HEALTH, new BotPlayerState().Health);
    }

    [Fact]
    public void HealthDamageAndRecoveryClampToResourceBounds()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 80 };
        state.ChangeHealth(-30, 100);
        Assert.Equal(50, state.Health);
        state.ChangeHealth(70, 100);
        Assert.Equal(100, state.Health);
        state.ChangeHealth(-150, 100);
        Assert.Equal(0, state.Health);
    }

    [Fact]
    public void PeriodicHealingAddsHealthAndDamageRemovesHealth()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 50 };
        void Apply(int health) => state.ChangeHealth(health, 100);
        state.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 10, 1, 1);
        state.TickPeriodicBuffs(100, Apply);
        Assert.Equal(60, state.Health);
        state.AddPeriodicBuff(BuffSubType.HEALTH_DOWN, 15, 1, 1);
        state.TickPeriodicBuffs(100, Apply);
        Assert.Equal(45, state.Health);
    }

    [Fact]
    public void SleepOnlyRecoversMissingHealth()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 99, State = PlayerState.SLEEP };
        var now = DateTime.UtcNow;
        state.GetSleepRecovery(now, false, 100);
        Assert.Equal(1, state.GetSleepRecovery(now.AddSeconds(1), false, 100));
        state.Health = 100;
        Assert.Equal(0, state.GetSleepRecovery(now.AddSeconds(2), false, 100));
    }

    [Fact]
    public void HealthChangesDoNotAffectOtherPlayers()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 100 };
        state.ChangeHealth(-1, 100);
        Assert.Equal(99, state.Health);
        Assert.Equal(Config.MAX_HEALTH, new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } }.Health);
    }

    [Fact]
    public void SleepWaitsForWarmupAndDoesNotReplayBlockedTicks()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, State = PlayerState.SLEEP, Health = 80 };
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
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 0 };
        state.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 10, 1, 2);
        state.AddPeriodicBuff(BuffSubType.HEALTH_ADD, 3, 1, 2);
        void Apply(int health) => state.ChangeHealth(health, 100);
        state.TickPeriodicBuffs(100, Apply);
        Assert.Equal(3, state.Health);
        state.TickPeriodicBuffs(100, Apply);
        Assert.Equal(6, state.Health);
        Assert.False(state.HasPeriodicBuffs);
    }
}
