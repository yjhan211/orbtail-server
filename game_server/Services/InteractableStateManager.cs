using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     단일 매칭 인스턴스의 Interactable 상태를 관리
/// </summary>
public class MatchingInteractableState
{
    // Key: (InteractId, ActionId), Value: InteractableActionState
    private readonly ConcurrentDictionary<(int, int), InteractableActionState> _actionStates = new();

    // Area별 오브젝트 ID 목록 (정적 데이터 캐시)
    private readonly ConcurrentDictionary<AreaType, List<int>> _areaObjects = new();

    public MatchingInteractableState()
    {
        InitializeFromData();
    }

    private void InitializeFromData()
    {
        var allInteractables = GameInteractableData.GetAll();

        foreach (var interactable in allInteractables)
        {
            var areaType = (AreaType)interactable.ZoneId;

            if (!_areaObjects.TryGetValue(areaType, out var objectIds))
            {
                objectIds = new List<int>();
                _areaObjects[areaType] = objectIds;
            }

            if (!objectIds.Contains(interactable.Id)) objectIds.Add(interactable.Id);

            // 액션별 상태 초기화
            foreach (var action in interactable.Actions)
            {
                var key = (interactable.Id, action.ActionId);
                _actionStates[key] = new InteractableActionState
                {
                    Order = action.ActionId,
                    IsExplored = false,
                    ExploredBy = 0
                };
            }
        }
    }

    public List<InteractableObjectState> GetAreaObjectStates(AreaType areaType)
    {
        if (!_areaObjects.TryGetValue(areaType, out var objectIds)) return new List<InteractableObjectState>();

        var result = new List<InteractableObjectState>();
        foreach (int interactId in objectIds)
        {
            var interactable = GameInteractableData.Get(interactId);

            // 동적 state 변경(사보타주 세대)은 퇴역 — 모든 Interactable은 기본 state 0.
            const int currentInteractableState = 0;

            var objectState = new InteractableObjectState
            {
                InteractId = interactId,
                Actions = new List<InteractableActionState>()
            };

            bool isRepeatable = IsRepeatableInteraction(interactable.InteractionType);

            bool hasUnexploredAction = false;
            foreach (var action in interactable.Actions)
            {
                // 모든 액션을 전송 (클라이언트에서 현재 state에 맞게 필터링)
                var key = (interactId, action.ActionId);
                if (_actionStates.TryGetValue(key, out var actionState))
                {
                    // 액션의 state 정보를 포함하여 전송
                    var actionWithState = new InteractableActionState
                    {
                        Order = actionState.Order,
                        IsExplored = actionState.IsExplored,
                        ExploredBy = actionState.ExploredBy,
                        State = action.State // CSV에서 정의된 state 값
                    };
                    objectState.Actions.Add(actionWithState);

                    // 현재 state와 일치하는 액션 중 탐색되지 않은 것이 있으면 표시
                    bool isActiveAction = action.State == 0 || action.State == currentInteractableState;
                    if (isActiveAction && (!actionState.IsExplored || isRepeatable)) hasUnexploredAction = true;
                }
            }

            // 탐색되지 않은 액션이 하나라도 있거나 repeatable 타입인 오브젝트만 포함
            if (hasUnexploredAction) result.Add(objectState);
        }

        return result;
    }

    private static bool IsRepeatableInteraction(InteractionType interactionType)
    {
        return interactionType is InteractionType.WRITING
            or InteractionType.VENT
            or InteractionType.MEETING
            or InteractionType.RNG_COLLECT;
    }
}

/// <summary>
///     MatchingId별로 Interactable 상태를 관리하는 매니저
///     각 매칭 인스턴스는 독립적인 상태를 가짐
/// </summary>
public class InteractableStateManager
{
    // Key: MatchingId, Value: 해당 매칭의 Interactable 상태
    private readonly ConcurrentDictionary<long, MatchingInteractableState> _matchingStates = new();
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _matchingStates.Clear();

        var allInteractables = GameInteractableData.GetAll();
        _logAction?.Invoke(
            $"InteractableStateManager: Loaded {allInteractables.Count} interactables from GameInteractableData (per-matching isolation enabled)");
    }

    /// <summary>
    ///     매칭 인스턴스의 상태를 가져오거나 새로 생성
    /// </summary>
    private MatchingInteractableState GetOrCreateMatchingState(long matchingId)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            _logAction?.Invoke($"InteractableStateManager: Creating new state for MatchingId={id}");
            return new MatchingInteractableState();
        });
    }

    /// <summary>
    ///     특정 Area의 오브젝트별 선택지 상태 반환
    /// </summary>
    public List<InteractableObjectState> GetAreaObjectStates(long matchingId, AreaType areaType)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        return matchingState.GetAreaObjectStates(areaType);
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"InteractableStateManager: Removed state for MatchingId={matchingId}");
    }
}
