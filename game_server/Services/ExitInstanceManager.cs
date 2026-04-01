using System.Collections.Concurrent;
using network.common.data;

namespace game_server.services;

/// <summary>
///     단일 매칭 인스턴스의 탈출 절차 상태
/// </summary>
public class MatchingExitState
{
    // 현재 단계의 완료 조건 추적
    private readonly HashSet<int> _acquiredItemIds = [];
    private readonly HashSet<string> _completedActions = []; // "interactableId_actionId" 형태

    private readonly object _stepLock = new();
    private readonly HashSet<int> _visitedAreas = [];

    public MatchingExitState(long matchingId)
    {
        var random = new Random((int)(matchingId % int.MaxValue));
        GenerateExitProcedure(random);
    }

    public int GroupId { get; private set; }
    public int CurrentStepOrder { get; private set; }
    public List<ExitStepData> Steps { get; private set; } = [];
    public bool IsCompleted { get; private set; }
    public long LastAdvancedBy { get; private set; }

    private void GenerateExitProcedure(Random random)
    {
        // 1. 사용 가능한 group_id 목록 수집
        var groupIds = new HashSet<int>();

        // group_id 1부터 시작해서 존재하는 그룹 찾기
        for (int groupId = 1; groupId <= 100; groupId++)
        {
            var steps = GameExitData.GetStepsByGroup(groupId);
            if (steps.Count > 0) groupIds.Add(groupId);
        }

        if (groupIds.Count == 0) throw new InvalidOperationException("No exit step groups found");

        // 2. 랜덤하게 그룹 선택
        var groupList = groupIds.ToList();
        GroupId = groupList[random.Next(groupList.Count)];

        // 3. 해당 그룹의 단계들 로드
        Steps = GameExitData.GetStepsByGroup(GroupId);
        CurrentStepOrder = 1;
        IsCompleted = false;
    }

    /// <summary>
    ///     현재 단계 정보 반환
    /// </summary>
    private ExitStepData? GetCurrentStep()
    {
        return IsCompleted ? null : Steps.FirstOrDefault(s => s.StepOrder == CurrentStepOrder);
    }

    /// <summary>
    ///     아이템 획득 시 호출
    /// </summary>
    public bool OnItemAcquired(int itemId)
    {
        lock (_stepLock)
        {
            _acquiredItemIds.Add(itemId);
            return CheckAndAdvance();
        }
    }

    /// <summary>
    ///     상호작용 액션 수행 시 호출
    /// </summary>
    public bool OnActionCompleted(int interactableId, int actionId)
    {
        lock (_stepLock)
        {
            string actionKey = $"{interactableId}_{actionId}";
            _completedActions.Add(actionKey);
            return CheckAndAdvance();
        }
    }

    /// <summary>
    ///     구역 방문 시 호출
    /// </summary>
    public bool OnAreaVisited(int areaType)
    {
        lock (_stepLock)
        {
            _visitedAreas.Add(areaType);
            return CheckAndAdvance();
        }
    }

    /// <summary>
    ///     현재 단계 완료 조건 확인 및 진행
    /// </summary>
    private bool CheckAndAdvance()
    {
        if (IsCompleted) return false;

        var currentStep = GetCurrentStep();
        if (currentStep == null) return false;

        // 모든 조건 확인
        bool allConditionsMet = true;

        // target_item_id 확인
        if (currentStep.TargetItemIds.Count > 0)
            foreach (int itemId in currentStep.TargetItemIds)
                if (!_acquiredItemIds.Contains(itemId))
                {
                    allConditionsMet = false;
                    break;
                }

        // target_interactable_action 확인 (CSV에서 "interactableId_actionId" 형태로 저장)
        if (allConditionsMet && currentStep.TargetInteractableActions.Count > 0)
            foreach (string actionKey in currentStep.TargetInteractableActions)
                if (!_completedActions.Contains(actionKey))
                {
                    allConditionsMet = false;
                    break;
                }

        // target_area 확인 (OR 조건: 하나라도 방문하면 충족)
        if (allConditionsMet && currentStep.TargetAreas.Count > 0)
        {
            bool anyAreaVisited = false;
            foreach (int areaType in currentStep.TargetAreas)
                if (_visitedAreas.Contains(areaType))
                {
                    anyAreaVisited = true;
                    break;
                }

            if (!anyAreaVisited) allConditionsMet = false;
        }

        if (allConditionsMet) return AdvanceToNextStep();

        return false;
    }

    /// <summary>
    ///     다음 단계로 진행
    /// </summary>
    private bool AdvanceToNextStep()
    {
        int nextOrder = CurrentStepOrder + 1;
        var nextStep = Steps.FirstOrDefault(s => s.StepOrder == nextOrder);

        if (nextStep == null)
        {
            // 모든 단계 완료 - 탈출 성공
            IsCompleted = true;
            return true;
        }

        CurrentStepOrder = nextOrder;

        // 다음 단계를 위해 추적 데이터 초기화하지 않음 (누적)
        return true;
    }

    /// <summary>
    ///     수동으로 다음 단계 진행 (플레이어에 의한 진행)
    /// </summary>
    public bool AdvanceStep(long playerId)
    {
        lock (_stepLock)
        {
            LastAdvancedBy = playerId;
            return AdvanceToNextStep();
        }
    }

    /// <summary>
    ///     현재 진행 상황 조회
    /// </summary>
    public (HashSet<int> items, HashSet<string> actions, HashSet<int> areas) GetProgress()
    {
        lock (_stepLock)
        {
            return (
                [.._acquiredItemIds],
                [.._completedActions],
                [.._visitedAreas]
            );
        }
    }
}

/// <summary>
///     MatchingId별로 탈출 절차 상태를 관리하는 매니저
/// </summary>
public class ExitInstanceManager
{
    private readonly ConcurrentDictionary<long, MatchingExitState> _matchingStates = new();
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;
        _matchingStates.Clear();
        _logAction?.Invoke("ExitInstanceManager: Initialized");
    }

    /// <summary>
    ///     매칭 인스턴스의 탈출 상태를 가져오거나 새로 생성
    /// </summary>
    public MatchingExitState GetOrCreateMatchingState(long matchingId)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            _logAction?.Invoke($"ExitInstanceManager: Creating new exit state for MatchingId={id}");
            var state = new MatchingExitState(id);
            _logAction?.Invoke(
                $"ExitInstanceManager: Generated - GroupId={state.GroupId}, TotalSteps={state.Steps.Count}");
            return state;
        });
    }

    /// <summary>
    ///     아이템 획득 시 탈출 진행 상황 업데이트
    /// </summary>
    public (bool advanced, bool escaped) OnItemAcquired(long matchingId, int itemId, long playerId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        int wasStep = state.CurrentStepOrder;

        if (state.OnItemAcquired(itemId))
        {
            _logAction?.Invoke(
                $"ExitInstanceManager: MatchingId={matchingId} advanced from step {wasStep} to {state.CurrentStepOrder} by acquiring item {itemId}");

            if (state.IsCompleted)
            {
                _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} ESCAPED!");
                return (true, true);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    ///     상호작용 액션 수행 시 탈출 진행 상황 업데이트
    /// </summary>
    public (bool advanced, bool escaped) OnActionCompleted(long matchingId, int interactableId, int actionId,
        long playerId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        int wasStep = state.CurrentStepOrder;

        if (state.OnActionCompleted(interactableId, actionId))
        {
            _logAction?.Invoke(
                $"ExitInstanceManager: MatchingId={matchingId} advanced from step {wasStep} to {state.CurrentStepOrder} by action {interactableId}_{actionId}");

            if (state.IsCompleted)
            {
                _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} ESCAPED!");
                return (true, true);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    ///     구역 방문 시 탈출 진행 상황 업데이트
    /// </summary>
    public (bool advanced, bool escaped) OnAreaVisited(long matchingId, int areaType, long playerId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        int wasStep = state.CurrentStepOrder;

        if (state.OnAreaVisited(areaType))
        {
            _logAction?.Invoke(
                $"ExitInstanceManager: MatchingId={matchingId} advanced from step {wasStep} to {state.CurrentStepOrder} by visiting area {areaType}");

            if (state.IsCompleted)
            {
                _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} ESCAPED!");
                return (true, true);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    ///     수동으로 다음 단계로 진행 (UI 버튼 클릭 등)
    /// </summary>
    public (bool success, bool escaped) AdvanceStep(long matchingId, long playerId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        int wasStep = state.CurrentStepOrder;

        if (state.AdvanceStep(playerId))
        {
            _logAction?.Invoke(
                $"ExitInstanceManager: MatchingId={matchingId} manually advanced from step {wasStep} to {state.CurrentStepOrder} by player {playerId}");

            if (state.IsCompleted)
            {
                _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} ESCAPED!");
                return (true, true);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    ///     탈출 완료 여부 확인
    /// </summary>
    public bool IsEscaped(long matchingId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var state))
            return false;
        return state.IsCompleted;
    }

    /// <summary>
    ///     매칭 종료 시 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"ExitInstanceManager: Removed state for MatchingId={matchingId}");
    }

    /// <summary>
    ///     전체 상태 초기화
    /// </summary>
    public void Reset()
    {
        _matchingStates.Clear();
        _logAction?.Invoke("ExitInstanceManager: All states cleared");
    }
}
