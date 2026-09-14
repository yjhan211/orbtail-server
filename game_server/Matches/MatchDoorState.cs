using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치별 문 개폐 상태를 관리한다.
///     초기 열린 문을 설정하고, 문 열기와 구역 폐쇄에 따른 닫기를 반영한다.
///     MatchRuntime이 소유하며, 호출자는 매치 잠금을 보유해야 한다.
///     종료 정리 후에는 다시 초기화하거나 문을 열 수 없다.
/// </summary>
internal sealed class MatchDoorState
{
    private readonly HashSet<int> _openDoors = [];
    private bool _initialized;
    private bool _cleared;

    public void Initialize(IEnumerable<AreaType>? initiallyLockedAreas = null)
    {
        var lockedAreas = initiallyLockedAreas?.ToHashSet() ?? [];
        int[] initiallyOpenDoorIds = GameDoorData.GetAll().Where(door => !lockedAreas.Contains(door.AreaType) && door.IsInitiallyOpen).Select(door => door.DoorId).ToArray();
        if (_cleared || _initialized)
        {
            return;
        }
        _openDoors.UnionWith(initiallyOpenDoorIds);
        _initialized = true;
    }

    public bool OpenDoor(int doorId)
    {
        if (_cleared)
        {
            return false;
        }
        _initialized = true;
        return _openDoors.Add(doorId);
    }

    public IReadOnlyList<int> CloseDoorsForAreas(IEnumerable<AreaType> areas)
    {
        int[] doorIds = areas.Distinct().SelectMany(GameDoorData.GetByAreaType).Select(door => door.DoorId).Distinct().ToArray();
        if (_cleared)
        {
            return [];
        }
        var changed = new List<int>();
        foreach (int doorId in doorIds)
        {
            if (_openDoors.Remove(doorId))
            {
                changed.Add(doorId);
            }
        }
        return changed;
    }

    public bool IsDoorOpen(int doorId)
    {
        return !_cleared && _openDoors.Contains(doorId);
    }

    public DoorInfoData? GetBlockingDoor(AreaType fromArea, AreaType toArea, Cell fromCell, Cell toCell, bool ignoreClosedDoors = false)
    {
        if (ignoreClosedDoors)
        {
            return null;
        }
        var door = GameDoorData.GetDoorForTransition(fromArea, toArea, fromCell, toCell);
        return door != null && !IsDoorOpen(door.DoorId) ? door : null;
    }

    public List<int> GetOpenDoors()
    {
        return _cleared ? [] : _openDoors.ToList();
    }

    public void Clear()
    {
        _cleared = true;
        _openDoors.Clear();
    }
}
