using game_server.sessions;
using game_server.services;
using network.common;
namespace demo_regression_tests;

public sealed class PlayerConditionTests
{
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
        Assert.Equal(Config.MAX_HEALTH, new PlayerCondition().Health);
        Assert.Equal(Config.MAX_HEALTH, new BotPlayerState().Health);
    }

    [Fact]
    public void HealthDamageAndRecoveryClampToResourceBounds()
    {
        var state = new PlayerCondition { Health = 80 };
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
        var state = new PlayerCondition { Health = 50 };
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
        var state = new PlayerCondition { Health = 99, IsSleeping = true };
        var now = DateTime.UtcNow;
        state.GetSleepRecovery(now, false, 100);
        Assert.Equal(1, state.GetSleepRecovery(now.AddSeconds(1), false, 100));
        state.Health = 100;
        Assert.Equal(0, state.GetSleepRecovery(now.AddSeconds(2), false, 100));
    }

    [Fact]
    public void HealthChangesDoNotAffectOtherPlayers()
    {
        var state = new PlayerCondition { Health = 100 };
        state.ChangeHealth(-1, 100);
        Assert.Equal(99, state.Health);
        Assert.Equal(Config.MAX_HEALTH, new PlayerCondition().Health);
    }

    [Fact]
    public void SleepWaitsForWarmupAndDoesNotReplayBlockedTicks()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new PlayerCondition { IsSleeping = true, Health = 80 };
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
        var state = new PlayerCondition { Health = 0 };
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
