using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     단일 매칭 인스턴스의 상호작용 규칙 상태
///     area_rule.csv 기반으로 금지된 액션을 판정한다.
///     페널티 적용은 interactable_action.csv + InteractableStateManager에서 처리.
/// </summary>
public class MatchingInteractRuleState
{
    // 이 매칭에 선택된 규칙들의 (TargetInteractId, TargetActionId) -> RuleId 매핑
    // TargetActionId가 0이면 해당 interactable의 모든 액션이 위반
    private readonly Dictionary<(int interactId, int actionId), int> _forbiddenActions = new();

    public MatchingInteractRuleState(List<int> selectedRuleIds)
    {
        foreach (int ruleId in selectedRuleIds)
        {
            var rule = GameAreaRuleData.Get(ruleId);
            if (rule.TargetInteractId <= 0) continue;
            var key = (rule.TargetInteractId, rule.TargetActionId);
            _forbiddenActions.TryAdd(key, ruleId);
        }
    }

    /// <summary>
    ///     금지된 액션인지 확인
    /// </summary>
    public bool IsForbiddenAction(int interactId, int actionId)
    {
        return _forbiddenActions.ContainsKey((interactId, actionId)) ||
               _forbiddenActions.ContainsKey((interactId, 0));
    }
}

/// <summary>
///     MatchingId별로 상호작용 규칙 상태를 관리하는 매니저
/// </summary>
public class InteractRuleManager
{
    private readonly ConcurrentDictionary<long, MatchingInteractRuleState> _matchingStates = new();
    private AreaRuleManager? _areaRuleManager;
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null, AreaRuleManager? areaRuleManager = null)
    {
        _logAction = logAction;
        _areaRuleManager = areaRuleManager;
        _matchingStates.Clear();
        _logAction?.Invoke("InteractRuleManager: Initialized");
    }

    /// <summary>
    ///     매칭 인스턴스의 상호작용 규칙 상태를 가져오거나 새로 생성
    /// </summary>
    private MatchingInteractRuleState GetOrCreateMatchingState(long matchingId)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            var allRules = _areaRuleManager?.GetAllRules(matchingId) ?? new Dictionary<AreaType, List<int>>();
            var allRuleIds = allRules.Values.SelectMany(r => r).ToList();

            var state = new MatchingInteractRuleState(allRuleIds);
            _logAction?.Invoke($"InteractRuleManager: Created state for MatchingId={id} with {allRuleIds.Count} rules");

            return state;
        });
    }

    /// <summary>
    ///     금지된 액션인지 확인
    /// </summary>
    public bool IsForbiddenAction(long matchingId, int interactId, int actionId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        return state.IsForbiddenAction(interactId, actionId);
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"InteractRuleManager: Removed state for MatchingId={matchingId}");
    }

    /// <summary>
    ///     전체 상태 초기화
    /// </summary>
    public void Reset()
    {
        _matchingStates.Clear();
        _logAction?.Invoke("InteractRuleManager: All matching states cleared");
    }
}
