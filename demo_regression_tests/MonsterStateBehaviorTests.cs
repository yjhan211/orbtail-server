using game_server.matches.monsters;

namespace demo_regression_tests;

public sealed class MonsterStateBehaviorTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LethalCombatRemovesTargetAndLateHitsCannotSettleAgain()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).GetOrCreate(987637);
        using var scope = runtime.Enter();
        runtime.Monsters.Initialize(Now);
        var monster = new Monster { MonsterId = 1, Alive = true, Health = 10 };
        runtime.Monsters.Entities[1] = monster;
        var combat = new MonsterCombatService();

        var result = combat.ApplyMonsterDamage(runtime, 1, 1, 10, Now);

        Assert.True(result.Killed);
        Assert.Same(monster, result.Monster);
        Assert.Empty(runtime.Monsters.Entities);
        Assert.Empty(runtime.Monsters.GetVisualStatesByArea());
        Assert.False(monster.ToMonsterInfo().IsAlive);
        Assert.False(combat.ApplyMonsterDamage(runtime, 1, 1, 10, Now).Applied);
    }
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
    public void CombatTargets_IncludeNewMonstersAndExcludeDeadMonsters()
    {
        var state = new MatchMonsters();
        var ready = new Monster { MonsterId = 1, Alive = true, Health = 10 };
        state.Entities[1] = ready;
        state.Entities[2] = new Monster { Alive = true, Health = 10 };
        state.Entities[3] = new Monster { Alive = false, Health = 10 };
        Assert.Equal(2, state.GetCombatTargets().Count);
        Assert.Contains(ready, state.GetCombatTargets());
        Assert.Contains(state.Entities[2], state.GetCombatTargets());
        Assert.Same(ready, state.FindAlive(1));
        ready.ApplyDamage(10, Now);
        Assert.Null(state.FindAlive(1));
        Assert.Same(ready, state.Find(1));
    }

    [Fact]
    public void RemovalImmediatelyExcludesMonsterAndIsIdempotent()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).GetOrCreate(987636);
        using var scope = runtime.Enter();
        var removed = new Monster { MonsterId = 1, Alive = true, Health = 10 };
        runtime.Monsters.Entities[1] = removed;
        runtime.Monsters.Entities[2] = new Monster { MonsterId = 2, Alive = true };

        runtime.RemoveMonster(removed);
        runtime.RemoveMonster(removed);

        Assert.False(removed.Alive);
        Assert.Single(runtime.Monsters.Entities);
        Assert.Null(runtime.Monsters.Find(1));
        Assert.False(removed.ApplyDamage(10, Now));
    }
}
