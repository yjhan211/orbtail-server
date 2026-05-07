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

    // Interactable별 현재 state (사보타주 등 동적 상태 변경용)
    private readonly ConcurrentDictionary<int, int> _interactableStates = new();

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

            // 현재 Interactable의 state 가져오기
            int currentInteractableState = GetInteractableState(interactId);

            var objectState = new InteractableObjectState
            {
                InteractId = interactId,
                Actions = new List<InteractableActionState>()
            };

            // InteractionType >= 3 (WRITING, RECEIVE_CALL, VENT 등)은 항상 선택 가능
            bool isRepeatable = (int)interactable.InteractionType >= 3;

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

    public bool TryExplore(int interactId, int actionId, long playerId, out InteractableActionState? state)
    {
        state = null;
        var key = (interactId, actionId);

        if (!_actionStates.TryGetValue(key, out var currentState)) return false;

        // InteractionType >= 3 (WRITING, RECEIVE_CALL, VENT 등)은 재선택 가능
        var interactable = GameInteractableData.Get(interactId);
        bool isRepeatable = (int)interactable.InteractionType >= 3;

        if (currentState.IsExplored && !isRepeatable)
        {
            state = currentState;
            return false;
        }

        var newState = new InteractableActionState { Order = actionId, IsExplored = true, ExploredBy = playerId };

        _actionStates[key] = newState;
        state = newState;

        // SINGLE 타입이면 해당 오브젝트의 모든 액션을 탐색 완료 처리
        if (interactable.InteractionType != InteractionType.SINGLE) return true;
        foreach (var action in interactable.Actions)
        {
            var otherKey = (interactId, action.ActionId);
            if (otherKey != key && _actionStates.TryGetValue(otherKey, out var otherState) &&
                !otherState.IsExplored)
                _actionStates[otherKey] = new InteractableActionState
                {
                    Order = action.ActionId,
                    IsExplored = true,
                    ExploredBy = playerId // 선택한 플레이어가 잠금
                };
        }

        return true;
    }

    public InteractableActionState? GetState(int interactId, int actionId)
    {
        var key = (interactId, actionId);
        return _actionStates.GetValueOrDefault(key);
    }

    /// <summary>
    ///     Interactable의 현재 state 가져오기 (기본값 0)
    /// </summary>
    public int GetInteractableState(int interactId)
    {
        return _interactableStates.GetValueOrDefault(interactId, 0);
    }

    /// <summary>
    ///     Interactable의 state 설정
    /// </summary>
    public void SetInteractableState(int interactId, int state)
    {
        _interactableStates[interactId] = state;
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
    ///     특정 선택지 탐색 처리
    /// </summary>
    public bool TryExplore(long matchingId, int interactId, int actionId, long playerId,
        out InteractableActionState? state)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        return matchingState.TryExplore(interactId, actionId, playerId, out state);
    }

    /// <summary>
    ///     특정 선택지 상태 조회
    /// </summary>
    public InteractableActionState? GetState(long matchingId, int interactId, int actionId)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        return matchingState.GetState(interactId, actionId);
    }

    /// <summary>
    ///     특정 액션의 결과 정보 반환 (ResultType, ResultId, ResultAmount).
    /// </summary>
    public (ActionResultType resultType, int resultId, int resultAmount) GetActionResult(int interactId, int actionId)
    {
        var interactable = GameInteractableData.Get(interactId);

        var action = interactable.Actions.FirstOrDefault(a => a.ActionId == actionId);
        if (action == null)
            return (ActionResultType.NONE, 0, 0);

        return (action.ResultType, action.ResultId, action.ResultAmount);
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"InteractableStateManager: Removed state for MatchingId={matchingId}");
    }

    /// <summary>
    ///     Interactable의 현재 state 가져오기
    /// </summary>
    public int GetInteractableState(long matchingId, int interactId)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        return matchingState.GetInteractableState(interactId);
    }

    /// <summary>
    ///     Interactable의 state 설정
    /// </summary>
    public void SetInteractableState(long matchingId, int interactId, int state)
    {
        var matchingState = GetOrCreateMatchingState(matchingId);
        matchingState.SetInteractableState(interactId, state);
        _logAction?.Invoke(
            $"InteractableStateManager: SetInteractableState MatchingId={matchingId}, InteractId={interactId}, State={state}");
    }

    /// <summary>
    ///     전체 상태 초기화
    /// </summary>
    public void Reset()
    {
        _matchingStates.Clear();
        _logAction?.Invoke("InteractableStateManager: All matching states cleared");
    }
}
