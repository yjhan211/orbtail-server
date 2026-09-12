using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchMonsterStateTests
{
    [Fact]
    public void SnapshotSlot_IsIndependentPerMatchAndAllowsBoundary()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947101);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947102);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var monsters = new MatchMonsterService(new MonsterMovementService());
        using (first.Enter())
        {
            Assert.True(monsters.TryClaimSnapshotSlot(first, now));
            Assert.False(monsters.TryClaimSnapshotSlot(first, now.AddMilliseconds(99)));
            Assert.True(monsters.TryClaimSnapshotSlot(first, now.AddMilliseconds(100)));
        }
        using (second.Enter()) Assert.True(monsters.TryClaimSnapshotSlot(second, now));
    }

    [Fact]
    public void VisualStatesByArea_GroupPerAreaSortedByMonsterId()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947103);
        for (int id = 12; id >= 1; id--)
        {
            runtime.Monsters.Entities[id] = new Monster { MonsterId = id, Area = AreaType.S2Classroom2, Alive = true };
        }
        runtime.Monsters.Entities[100] = new Monster { MonsterId = 100, Area = AreaType.S2Gym1, Alive = true };
        runtime.Monsters.Entities[200] = new Monster { MonsterId = 200, Area = AreaType.None, Alive = true };

        IReadOnlyDictionary<AreaType, List<MonsterRuntimeInfo>> groups;
        using (runtime.Enter())
        {
            groups = new MatchMonsterService(new MonsterMovementService()).GetVisualStatesByArea(runtime);
        }

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.All(group.Value, monster => Assert.Equal(group.Key, monster.AreaType)));
        Assert.Equal(Enumerable.Range(1, 12), groups[AreaType.S2Classroom2].Select(monster => monster.MonsterId));
        Assert.Single(groups[AreaType.S2Gym1]);
    }
}
