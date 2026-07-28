using network.common;
using network.common.data.models;

namespace game_server.services;

public static class MonsterSnapshotBatcher
{
    public const int DefaultChunkSize = 10;

    public static IReadOnlyList<MonsterAreaSnapshotChunk> CreateAreaChunks(
        IEnumerable<MonsterRuntimeInfo> states,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

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

        var chunks = new List<MonsterAreaSnapshotChunk>();
        foreach (var (area, areaStates) in statesByArea.OrderBy(entry => entry.Key))
        {
            areaStates.Sort(static (left, right) => left.MonsterId.CompareTo(right.MonsterId));
            for (int index = 0; index < areaStates.Count; index += chunkSize)
            {
                int count = Math.Min(chunkSize, areaStates.Count - index);
                chunks.Add(new MonsterAreaSnapshotChunk(area, areaStates.GetRange(index, count)));
            }
        }
        return chunks;
    }
}

public sealed class MonsterSnapshotAccumulator
{
    private readonly Dictionary<int, MonsterRuntimeInfo> _finalStatesByMonsterId = new();

    public int Count => _finalStatesByMonsterId.Count;

    public void Record(MonsterRuntimeInfo state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.MonsterId <= 0) return;
        _finalStatesByMonsterId[state.MonsterId] = state;
    }

    public IReadOnlyList<MonsterRuntimeInfo> GetFinalStates() =>
        _finalStatesByMonsterId.Values.OrderBy(state => state.MonsterId).ToList();
}

public readonly record struct MonsterAreaSnapshotChunk(AreaType Area, List<MonsterRuntimeInfo> Monsters);
