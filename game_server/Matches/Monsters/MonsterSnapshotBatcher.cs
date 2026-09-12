using network.common;
using network.common.data.models;

namespace game_server.matches.monsters;

public static class MonsterSnapshotBatcher
{
    public static IReadOnlyList<MonsterAreaSnapshot> GroupByArea(IEnumerable<MonsterRuntimeInfo> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var statesByArea = new Dictionary<AreaType, List<MonsterRuntimeInfo>>();
        foreach (var state in states)
        {
            if (state.MonsterId <= 0 || state.AreaType == AreaType.None)
            {
                continue;
            }
            if (!statesByArea.TryGetValue(state.AreaType, out var areaStates))
            {
                areaStates = new List<MonsterRuntimeInfo>();
                statesByArea[state.AreaType] = areaStates;
            }
            areaStates.Add(state);
        }

        var groups = new List<MonsterAreaSnapshot>();
        foreach (var (area, areaStates) in statesByArea.OrderBy(entry => entry.Key))
        {
            areaStates.Sort(static (left, right) => left.MonsterId.CompareTo(right.MonsterId));
            groups.Add(new MonsterAreaSnapshot(area, areaStates));
        }
        return groups;
    }
}

public readonly record struct MonsterAreaSnapshot(AreaType Area, List<MonsterRuntimeInfo> Monsters);
