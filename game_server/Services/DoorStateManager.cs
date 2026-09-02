using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     매칭별 문 상태를 관리하는 서비스
///     게임 세션 동안 문 상태 유지 (한번 열리면 계속 Open)
/// </summary>
public class DoorStateManager
{
    private readonly ConcurrentDictionary<long, MatchingDoorState> _states = new();

    /// <summary>
    ///     Preregisters fail-closed state at the match runtime boundary. Gameplay methods never
    ///     create missing state, so terminal cleanup cannot be undone by a late door action.
    /// </summary>
    public bool RegisterMatching(long matchingId) =>
        _states.TryAdd(matchingId, new MatchingDoorState());

    /// <summary>
    ///     매칭 시작 시 초기 열린 문을 등록한다. Production callers invoke this only inside
    ///     an active match runtime operation; direct state creation remains for standalone fixtures.
    /// </summary>
    public void InitializeMatching(long matchingId, IEnumerable<AreaType>? initiallyLockedAreas = null)
    {
        var lockedAreas = initiallyLockedAreas?.ToHashSet() ?? [];
        int[] initiallyOpenDoorIds = GameDoorData.GetAll()
            .Where(door => !lockedAreas.Contains(door.AreaType) && door.IsInitiallyOpen)
            .Select(door => door.DoorId)
            .ToArray();
        MatchingDoorState state = _states.GetOrAdd(
            matchingId,
            static _ => new MatchingDoorState());
        lock (state.SyncRoot)
        {
            if (state.Cleared || state.Initialized)
                return;

            state.OpenDoors.UnionWith(initiallyOpenDoorIds);
            state.Initialized = true;
        }
    }

    /// <summary>
    ///     문 열기
    /// </summary>
    /// <returns>true: 새로 열림, false: 이미 열려있었음</returns>
    public bool OpenDoor(long matchingId, int doorId)
    {
        if (!_states.TryGetValue(matchingId, out MatchingDoorState? state))
            return false;

        lock (state.SyncRoot)
        {
            if (state.Cleared)
                return false;

            // Preserve the legacy OpenDoor-before-Initialize behavior for a preregistered state:
            // the first explicit mutation owns an otherwise empty initial set.
            state.Initialized = true;
            return state.OpenDoors.Add(doorId);
        }
    }

    public IReadOnlyList<int> CloseDoorsForAreas(long matchingId, IEnumerable<AreaType> areas)
    {
        if (!_states.TryGetValue(matchingId, out MatchingDoorState? state))
            return [];

        int[] doorIds = GetDoorIds(areas).ToArray();
        lock (state.SyncRoot)
        {
            if (state.Cleared)
                return [];

            var changed = new List<int>();
            foreach (int doorId in doorIds)
            {
                if (state.OpenDoors.Remove(doorId))
                    changed.Add(doorId);
            }

            return changed;
        }
    }

    private static IEnumerable<int> GetDoorIds(IEnumerable<AreaType> areas) =>
        areas
            .Distinct()
            .SelectMany(GameDoorData.GetByAreaType)
            .Select(door => door.DoorId)
            .Distinct();

    /// <summary>
    ///     문이 열려있는지 확인
    /// </summary>
    public bool IsDoorOpen(long matchingId, int doorId)
    {
        if (!_states.TryGetValue(matchingId, out MatchingDoorState? state))
            return false;

        lock (state.SyncRoot)
        {
            return !state.Cleared && state.OpenDoors.Contains(doorId);
        }
    }

    /// <summary>
    ///     열린 문 ID 목록 가져오기
    /// </summary>
    public List<int> GetOpenDoors(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out MatchingDoorState? state))
            return [];

        lock (state.SyncRoot)
        {
            return state.Cleared ? [] : state.OpenDoors.ToList();
        }
    }

    /// <summary>
    ///     매칭 종료 시 상태 정리
    /// </summary>
    public void ClearMatching(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out MatchingDoorState? state))
            return;

        lock (state.SyncRoot)
        {
            state.Cleared = true;
            state.OpenDoors.Clear();
        }

        ((ICollection<KeyValuePair<long, MatchingDoorState>>)_states)
            .Remove(new KeyValuePair<long, MatchingDoorState>(matchingId, state));
    }

    private sealed class MatchingDoorState
    {
        public object SyncRoot { get; } = new();
        public HashSet<int> OpenDoors { get; } = [];
        public bool Initialized { get; set; }
        public bool Cleared { get; set; }
    }
}
