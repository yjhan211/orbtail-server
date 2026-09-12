using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchMonsterStateTests
{
    [Fact]
    public void SnapshotSlot_IsIndependentPerMatchAndAllowsBoundary()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947101);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947102);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(first.Monsters.TryClaimSnapshotSlot(now));
        Assert.False(first.Monsters.TryClaimSnapshotSlot(now.AddMilliseconds(99)));
        Assert.True(first.Monsters.TryClaimSnapshotSlot(now.AddMilliseconds(100)));
        Assert.True(second.Monsters.TryClaimSnapshotSlot(now));
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

        var groups = runtime.Monsters.GetVisualStatesByArea();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.All(group.Value, monster => Assert.Equal(group.Key, monster.AreaType)));
        Assert.Equal(Enumerable.Range(1, 12), groups[AreaType.S2Classroom2].Select(monster => monster.MonsterId));
        Assert.Single(groups[AreaType.S2Gym1]);
    }
}
