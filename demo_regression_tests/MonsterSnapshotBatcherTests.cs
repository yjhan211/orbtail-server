using game_server.monsters;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class MonsterSnapshotBatcherTests
{
    [Fact]
    public void GroupByArea_OneGroupPerAreaSortedByMonsterId()
    {
        var states = Enumerable.Range(1, 12)
            .Select(id => State(id, AreaType.S2Classroom2, 48 - id))
            .Append(State(100, AreaType.S2Gym1, 40))
            .Reverse()
            .ToList();

        var groups = MonsterSnapshotBatcher.GroupByArea(states);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.All(group.Monsters,
            monster => Assert.Equal(group.Area, monster.AreaType)));
        Assert.Equal(Enumerable.Range(1, 12), groups
            .Single(group => group.Area == AreaType.S2Classroom2)
            .Monsters
            .Select(monster => monster.MonsterId));
    }

    private static MonsterRuntimeInfo State(int monsterId, AreaType area, int health, bool isAlive = true) => new()
    {
        MonsterId = monsterId,
        AreaType = area,
        MaxHealth = 48,
        CurrentHealth = health,
        IsAlive = isAlive
    };
}
