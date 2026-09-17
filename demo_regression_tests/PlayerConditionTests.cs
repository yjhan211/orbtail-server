using game_server.matches;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using network.common;
using network.common.data.models;
namespace demo_regression_tests;

public sealed class PlayerConditionTests
{
    // 위치·셀 변환은 맵 정보가 있어야 한다 — 실행 순서와 무관하게 게임 데이터를 먼저 올린다.
    public PlayerConditionTests() => UserServerMatchingTestData.EnsureGameDataLoaded();

    // 수면 회복 계산은 탈락 처리나 전송 의존성을 사용하지 않는다.
    private readonly PlayerHealthService _health = new(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerHealthService>.Instance);

    [Fact]
    public void IdentityIsInitializedBeforeSpawnWithoutPublishingPosition()
    {
        var player = new Player(new PlayerInfo { PlayerId = 42 });
        Assert.Equal(42, player.GameInfo.ObjectInfo.ObjectId);
        Assert.Equal(Config.SWARM_MATCH_MAP, player.GameInfo.ObjectInfo.MapId);
        Assert.Null(player.Position);
        Assert.Null(player.Cell);
        Assert.Throws<InvalidOperationException>(() => player.CreateGameObjectInfo());

        var bot = new Bot { PlayerId = -42 };
        Assert.Equal(-42, bot.Player.GameInfo.ObjectInfo.ObjectId);
    }
    [Fact]
    public void RecoveryResultDistinguishesRequestedAndActualAmount()
    {
        var condition = new Player(new PlayerInfo { PlayerId = 1 }) { Health = Config.MAX_HEALTH - 3 };
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
    public void LeavingSleepResetsRecovery(PlayerState nextState)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 50 };
        Assert.True(condition.TryStartSleep());
        Assert.Equal(PlayerState.SLEEP, condition.State);
        _health.GetSleepRecovery(condition, now, 100);
        Assert.Equal(5, _health.GetSleepRecovery(condition, now.AddSeconds(1), 100));

        condition.State = nextState;
        Assert.False(condition.IsSleeping);
        Assert.False(condition.StatusEffects.HasSleep());
        Assert.Equal(0, _health.GetSleepRecovery(condition, now.AddSeconds(10), 100));
        Assert.Equal(50, condition.Health);

        Assert.True(condition.TryStartSleep());
        Assert.Equal(0, _health.GetSleepRecovery(condition, now.AddSeconds(10), 100));
        Assert.Equal(5, _health.GetSleepRecovery(condition, now.AddSeconds(11), 100));
    }

    [Fact]
    public void DamageResultClampsAtZeroAndDoesNotOverflow()
    {
        var condition = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 7 };
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
    public void StartSleepIgnoresHealingLockAndDoesNotRestartSleep()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var condition = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 50 };
        condition.StatusEffects.Apply(PlayerStatusEffectKind.HealingBlocked, now.AddSeconds(5));
        Assert.True(condition.TryStartSleep());
        Assert.True(condition.IsSleeping);
        Assert.Equal(0, _health.GetSleepRecovery(condition, now.AddSeconds(5), Config.MAX_HEALTH));
        Assert.False(condition.TryStartSleep());
        Assert.True(_health.GetSleepRecovery(condition, now.AddSeconds(6), Config.MAX_HEALTH) > 0);
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
        Assert.Equal(Config.MAX_HEALTH, new Player(new PlayerInfo { PlayerId = 1 }).Health);
        Assert.Equal(Config.MAX_HEALTH, new Bot().Player.Health);
    }

    [Fact]
    public void HealthDamageAndRecoveryClampToResourceBounds()
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 80 };
        state.ChangeHealth(-30, 100);
        Assert.Equal(50, state.Health);
        state.ChangeHealth(70, 100);
        Assert.Equal(100, state.Health);
        state.ChangeHealth(-150, 100);
        Assert.Equal(0, state.Health);
    }


    [Fact]
    public void SleepOnlyRecoversMissingHealth()
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 99, State = PlayerState.SLEEP };
        var now = DateTime.UtcNow;
        _health.GetSleepRecovery(state, now, 100);
        Assert.Equal(1, _health.GetSleepRecovery(state, now.AddSeconds(1), 100));
        state.Health = 100;
        Assert.Equal(0, _health.GetSleepRecovery(state, now.AddSeconds(2), 100));
    }

    [Fact]
    public void SleepEffectControlsStateAndRepeatedSleepDoesNotResetRecovery()
    {
        var player = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 50 };
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        player.State = PlayerState.SLEEP;
        Assert.True(player.StatusEffects.HasSleep());
        Assert.True(player.IsSleeping);
        Assert.Equal(PlayerState.SLEEP, player.GameInfo.State);
        Assert.Equal(0, _health.GetSleepRecovery(player, now, 100));
        player.State = PlayerState.SLEEP;
        Assert.Equal(5, _health.GetSleepRecovery(player, now.AddSeconds(1), 100));
        Assert.True(player.TryStopSleep());
        Assert.False(player.StatusEffects.HasSleep());
        Assert.Equal(PlayerState.IDLE, player.State);
        Assert.Equal(PlayerState.IDLE, player.GameInfo.State);
        Assert.Equal(0, _health.GetSleepRecovery(player, now.AddSeconds(2), 100));
    }

    [Fact]
    public void HealthChangesDoNotAffectOtherPlayers()
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 }) { Health = 100 };
        state.ChangeHealth(-1, 100);
        Assert.Equal(99, state.Health);
        Assert.Equal(Config.MAX_HEALTH, new Player(new PlayerInfo { PlayerId = 1 }).Health);
    }

    [Fact]
    public void SleepWaitsForWarmupAndDoesNotReplayBlockedTicks()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new Player(new PlayerInfo { PlayerId = 1 }) { State = PlayerState.SLEEP, Health = 80 };
        Assert.Equal(0, _health.GetSleepRecovery(state, start, 100));
        Assert.Equal(0, _health.GetSleepRecovery(state, start.AddMilliseconds(999), 100));
        Assert.Equal(5, _health.GetSleepRecovery(state, start.AddSeconds(1), 100));
        Assert.Equal(0, _health.GetSleepRecovery(state, start.AddSeconds(1), 100));
        state.StatusEffects.Apply(PlayerStatusEffectKind.HealingBlocked, start.AddSeconds(4));
        Assert.Equal(0, _health.GetSleepRecovery(state, start.AddSeconds(3), 100));
        Assert.Equal(5, _health.GetSleepRecovery(state, start.AddSeconds(4), 100));
    }

}
