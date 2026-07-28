using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimageMonsterManagerTests
{
    [Fact]
    public void Damage_ResetsAfterLeashDelay_WhenNoPlayerRemainsInRange()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);

        var hit = manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 7, now);

        Assert.True(hit.StateChanged);
        Assert.False(hit.Killed);
        Assert.Equal(41, hit.State!.CurrentHealth);

        var tick = manager.Tick(202, [], now.AddSeconds(6));

        var reset = Assert.Single(tick.ChangedStates);
        Assert.True(reset.IsAlive);
        Assert.Equal(reset.MaxHealth, reset.CurrentHealth);
    }

    [Fact]
    public void LethalDamage_DespawnsWithoutTimeBasedRespawn()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);

        var lethal = manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 999, now);

        Assert.True(lethal.Killed);
        Assert.Equal(107000010, lethal.RewardItemId);
        Assert.False(lethal.State!.IsAlive);
        Assert.Empty(manager.Tick(202, [], now.AddSeconds(60)).ChangedStates);
    }


    [Fact]
    public void Tick_ChasesHighestDamageTargetBeforeContact()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);
        manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 1, now);
        float initialX = Assert.Single(manager.GetSnapshot(202)).PositionX;

        var tick = manager.Tick(202,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, new Vector3f(19f, 55.68081f, 0f))
        ], now.AddMilliseconds(50));

        var moved = Assert.Single(tick.ChangedStates);
        Assert.True(moved.PositionX > initialX);
        Assert.Empty(tick.Attacks);
    }
    [Fact]
    public void Tick_PrefersHighestTotalDamageWhenSeveralPlayersAreInLeashRange()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);
        manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 20, 1, now);
        manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 3, now);

        var attacks = manager.Tick(202,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, new Vector3f(16.4f, 55.7f, 0f)),
            new MonsterSpatialTarget(20, MapId.School, AreaType.Classroom4, new Vector3f(16.5f, 55.8f, 0f))
        ], now).Attacks;

        Assert.Equal(10, Assert.Single(attacks).TargetPlayerId);
    }
}