using network.common;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     몬스터 스냅샷을 구역별로 묶는다. 클라이언트는 자기 구역만 그리므로 패킷도 구역 단위다.
///     크기 분할은 하지 않는다 — 메시지 상한(MAX_MESSAGE_SIZE) 안에서 한 구역이 한 패킷이다.
/// </summary>
public static class MonsterSnapshotBatcher
{
    public static IReadOnlyList<MonsterAreaSnapshot> GroupByArea(IEnumerable<MonsterRuntimeInfo> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var statesByArea = new Dictionary<AreaType, List<MonsterRuntimeInfo>>();
        foreach (var state in states)
        {
            if (state == null || state.MonsterId <= 0 || state.AreaType == AreaType.None)
                continue;
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
