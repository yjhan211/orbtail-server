using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     매치 하나의 열린 문을 보관한다. MatchRuntime이 생성과 종료를 책임진다.
///     연결별 문 요청도 접근하므로 내부 잠금으로 보호하며, 종료 후에는 다시 초기화하거나 열 수 없다.
/// </summary>
internal sealed class MatchDoorState
{
    private readonly object _doorStateLock = new();
    private readonly HashSet<int> _openDoors = [];
    private bool _initialized;
    private bool _cleared;

    public void Initialize(IEnumerable<AreaType>? initiallyLockedAreas = null)
    {
        var lockedAreas = initiallyLockedAreas?.ToHashSet() ?? [];
        int[] initiallyOpenDoorIds = GameDoorData.GetAll()
            .Where(door => !lockedAreas.Contains(door.AreaType) && door.IsInitiallyOpen)
            .Select(door => door.DoorId)
            .ToArray();
        lock (_doorStateLock)
        {
            if (_cleared || _initialized)
                return;
            _openDoors.UnionWith(initiallyOpenDoorIds);
            _initialized = true;
        }
    }

    public bool OpenDoor(int doorId)
    {
        lock (_doorStateLock)
        {
            if (_cleared)
                return false;
            _initialized = true;
            return _openDoors.Add(doorId);
        }
    }

    public IReadOnlyList<int> CloseDoorsForAreas(IEnumerable<AreaType> areas)
    {
        int[] doorIds = areas.Distinct()
            .SelectMany(GameDoorData.GetByAreaType)
            .Select(door => door.DoorId).Distinct().ToArray();
        lock (_doorStateLock)
        {
            if (_cleared)
                return [];
            var changed = new List<int>();
            foreach (int doorId in doorIds)
                if (_openDoors.Remove(doorId))
                    changed.Add(doorId);
            return changed;
        }
    }

    public bool IsDoorOpen(int doorId)
    {
        lock (_doorStateLock)
            return !_cleared && _openDoors.Contains(doorId);
    }

    public List<int> GetOpenDoors()
    {
        lock (_doorStateLock)
            return _cleared ? [] : _openDoors.ToList();
    }

    public void Clear()
    {
        lock (_doorStateLock)
        {
            _cleared = true;
            _openDoors.Clear();
        }
    }
}
