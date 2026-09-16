using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchMonstersTests
{
    [Fact]
    public void VisualStatesByArea_GroupPerAreaSortedByMonsterId()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947103);
        for (int id = 12; id >= 1; id--)
        {
            runtime.Monsters.Entities[id] = new Monster { MonsterId = id, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Classroom2))), Alive = true };
        }
        runtime.Monsters.Entities[100] = new Monster { MonsterId = 100, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Gym1))), Alive = true };
        runtime.Monsters.Entities[200] = new Monster { MonsterId = 200, Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.None))), Alive = true };

        IReadOnlyDictionary<AreaType, List<MonsterInfo>> groups;
        using (runtime.Enter())
        {
            groups = runtime.Monsters.GetVisualStatesByArea();
        }

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.All(group.Value, monster => Assert.Equal(group.Key, monster.AreaType)));
        Assert.Equal(Enumerable.Range(1, 12), groups[AreaType.S2Classroom2].Select(monster => monster.MonsterId));
        Assert.Single(groups[AreaType.S2Gym1]);
    }
}
