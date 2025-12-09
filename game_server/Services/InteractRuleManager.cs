using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services
{
    /// <summary>
    /// 상호작용 규칙 위반 결과
    /// </summary>
    public class InteractViolationResult
    {
        public bool IsViolation { get; set; }
        public int ViolatedRuleId { get; set; }
        public int CorruptionDelta { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// 단일 매칭 인스턴스의 상호작용 규칙 상태
    /// </summary>
    public class MatchingInteractRuleState
    {
        // 이 매칭에 선택된 규칙들의 TargetInteractId -> RuleId 매핑
        private readonly Dictionary<int, int> _forbiddenInteracts = new();

        // 위반당 정신오염도 증가량
        private const int CorruptionPerViolation = 5;

        public MatchingInteractRuleState(List<int> selectedRuleIds)
        {
            foreach (var ruleId in selectedRuleIds)
            {
                var rule = GameAreaRuleData.Get(ruleId);
                if (rule != null && rule.TargetInteractId > 0)
                {
                    // 같은 interactId에 여러 규칙이 있을 수 있으므로 첫 번째만 저장
                    if (!_forbiddenInteracts.ContainsKey(rule.TargetInteractId))
                    {
                        _forbiddenInteracts[rule.TargetInteractId] = ruleId;
                    }
                }
            }
        }

        /// <summary>
        /// 오브젝트 탐색 시 규칙 위반 체크
        /// </summary>
        public InteractViolationResult CheckExplore(int interactId)
        {
            var result = new InteractViolationResult();

            if (_forbiddenInteracts.TryGetValue(interactId, out var ruleId))
            {
                var rule = GameAreaRuleData.Get(ruleId);
                result.IsViolation = true;
                result.ViolatedRuleId = ruleId;
                result.CorruptionDelta = CorruptionPerViolation;
                result.Message = $"금지된 오브젝트를 탐색했습니다: {rule?.Description ?? "알 수 없는 규칙"}";
            }

            return result;
        }

        /// <summary>
        /// 금지된 오브젝트인지 확인만 (위반 처리 없이)
        /// </summary>
        public bool IsForbiddenInteract(int interactId)
        {
            return _forbiddenInteracts.ContainsKey(interactId);
        }

        /// <summary>
        /// 특정 interactId에 해당하는 규칙 ID 반환 (없으면 0)
        /// </summary>
        public int GetRuleIdForInteract(int interactId)
        {
            return _forbiddenInteracts.TryGetValue(interactId, out var ruleId) ? ruleId : 0;
        }
    }

    /// <summary>
    /// MatchingId별로 상호작용 규칙 상태를 관리하는 매니저
    /// </summary>
    public class InteractRuleManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingInteractRuleState> _matchingStates = new();
        private AreaRuleManager? _areaRuleManager;

        public void Initialize(Action<string>? logAction = null, AreaRuleManager? areaRuleManager = null)
        {
            _logAction = logAction;
            _areaRuleManager = areaRuleManager;
            _matchingStates.Clear();
            _logAction?.Invoke("InteractRuleManager: Initialized");
        }

        /// <summary>
        /// 매칭 인스턴스의 상호작용 규칙 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingInteractRuleState GetOrCreateMatchingState(long matchingId)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                // AreaRuleManager에서 이 매칭에 선택된 모든 규칙 ID 가져오기
                var allRules = _areaRuleManager?.GetAllRules(matchingId) ?? new Dictionary<AreaType, List<int>>();
                var allRuleIds = allRules.Values.SelectMany(r => r).ToList();

                var state = new MatchingInteractRuleState(allRuleIds);
                _logAction?.Invoke($"InteractRuleManager: Created state for MatchingId={id} with {allRuleIds.Count} rules");

                return state;
            });
        }

        /// <summary>
        /// 오브젝트 탐색 시 규칙 위반 체크
        /// </summary>
        public InteractViolationResult CheckExplore(long matchingId, int interactId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            var result = state.CheckExplore(interactId);

            if (result.IsViolation)
            {
                _logAction?.Invoke($"InteractRuleManager: MatchingId={matchingId} violated rule {result.ViolatedRuleId} by exploring InteractId={interactId}");
            }

            return result;
        }

        /// <summary>
        /// 금지된 오브젝트인지 확인
        /// </summary>
        public bool IsForbiddenInteract(long matchingId, int interactId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.IsForbiddenInteract(interactId);
        }

        /// <summary>
        /// 매칭 종료 시 해당 매칭의 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingStates.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"InteractRuleManager: Removed state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingStates.Clear();
            _logAction?.Invoke("InteractRuleManager: All matching states cleared");
        }
    }
}
