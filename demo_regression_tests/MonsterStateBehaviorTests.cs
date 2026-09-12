using game_server.matches.monsters;

namespace demo_regression_tests;

public sealed class MonsterStateBehaviorTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LethalDamage_RecordsDeathOnce()
    {
        var monster = new Monster { Alive = true, Health = 10 };
        Assert.False(monster.ApplyDamage(0, Now));
        Assert.False(monster.ApplyDamage(4, Now));
        Assert.Equal(6, monster.Health);
        Assert.True(monster.ApplyDamage(20, Now));
        Assert.Equal(0, monster.Health);
        Assert.False(monster.Alive);
        Assert.Equal(Now, monster.DiedAtUtc);
        Assert.False(monster.ApplyDamage(1, Now.AddSeconds(1)));
        Assert.Equal(Now, monster.DiedAtUtc);
    }

    [Fact]
    public void DeadMonster_OnlyReleasesPreviouslyReservedDamage()
    {
        var monster = new Monster { Alive = true, Health = 10 };
        monster.ReserveDamage(6);
        monster.ApplySlow(2, Now);
        monster.ApplyDamage(10, Now);

        monster.ReserveDamage(9);
        monster.ApplySlow(10, Now);
        Assert.Equal(6, monster.PendingDamage);
        Assert.Equal(Now.AddSeconds(2), monster.WaveSlowUntilUtc);
        monster.ReleaseReservedDamage(3);
        Assert.Equal(3, monster.PendingDamage);
        monster.ReleaseReservedDamage(99);
        Assert.Equal(0, monster.PendingDamage);
    }

    [Fact]
    public void CombatTargets_ExcludeInactiveDeadAndFullyReservedMonsters()
    {
        var state = new MatchMonsters();
        var ready = new Monster { MonsterId = 1, CombatTargetId = -11, Alive = true, Health = 10, ActivatesAtUtc = Now };
        state.Entities[1] = ready;
        state.Entities[2] = new Monster { Alive = true, Health = 10, ActivatesAtUtc = Now.AddSeconds(1) };
        state.Entities[3] = new Monster { Alive = false, Health = 10 };
        state.Entities[4] = new Monster { Alive = true, Health = 10, PendingDamage = 10 };
        Assert.Same(ready, Assert.Single(state.GetCombatTargets(Now)));
        Assert.Same(ready, state.FindAliveByCombatTarget(-11));
        ready.ApplyDamage(10, Now);
        Assert.Null(state.FindAliveByCombatTarget(-11));
        Assert.Same(ready, state.FindByCombatTarget(-11));
    }

    [Fact]
    public void DeadPruning_PreservesThreeSecondBoundaryAndLivingEntities()
    {
        var state = new MatchMonsters();
        state.Entities[1] = new Monster { MonsterId = 1, Alive = false, DiedAtUtc = Now };
        state.Entities[2] = new Monster { MonsterId = 2, Alive = true };
        state.RemoveExpiredDead(Now.AddSeconds(3));
        Assert.Equal(2, state.Entities.Count);
        state.RemoveExpiredDead(Now.AddSeconds(3).AddTicks(1));
        Assert.Single(state.Entities);
        Assert.True(state.Entities.ContainsKey(2));
    }
}
