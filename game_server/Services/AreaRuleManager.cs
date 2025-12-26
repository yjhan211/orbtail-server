using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services
{
    /// <summary>
    /// 단일 매칭 인스턴스의 Area별 규칙 상태
    /// </summary>
    public class MatchingAreaRuleState
    {
        // Area별 선택된 규칙 ID 목록
        private readonly Dictionary<AreaType, List<int>> _selectedRules;

        // 셔플된 규칙 큐 (쪽지 아이템 사용 시 순차적으로 반환)
        private readonly Queue<int> _shuffledRuleQueue;
        private readonly object _queueLock = new();

        // 이번 매칭에 적용된 복도 규칙 ID
        public int CorridorRuleId { get; }

        public MatchingAreaRuleState(int corridorRuleId, Random? random = null)
        {
            random ??= new Random();
            CorridorRuleId = corridorRuleId;
            _selectedRules = GameAreaRuleData.SelectRulesForAllAreas(random);

            // 모든 규칙을 모아서 셔플 (복도 규칙은 CorridorAlertUI에서 별도 표시)
            var allRules = _selectedRules.Values.SelectMany(r => r).ToList();
            Shuffle(allRules, random);

            _shuffledRuleQueue = new Queue<int>(allRules);
        }

        private static void Shuffle<T>(IList<T> list, Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// 특정 Area의 선택된 규칙 ID 목록
        /// </summary>
        public List<int> GetRulesForArea(AreaType areaType)
        {
            return _selectedRules.TryGetValue(areaType, out var rules) ? rules : new List<int>();
        }

        /// <summary>
        /// 모든 Area의 선택된 규칙
        /// </summary>
        public Dictionary<AreaType, List<int>> GetAllRules()
        {
            return new Dictionary<AreaType, List<int>>(_selectedRules);
        }

        /// <summary>
        /// 전체 규칙 ID 목록 (flat)
        /// </summary>
        public List<int> GetAllRuleIds()
        {
            return _selectedRules.Values.SelectMany(r => r).ToList();
        }

        /// <summary>
        /// 큐에서 다음 규칙 ID를 가져옴 (쪽지 아이템 사용 시 호출)
        /// 모든 플레이어가 같은 순서로 규칙을 받음
        /// </summary>
        /// <returns>규칙 ID, 큐가 비어있으면 0</returns>
        public int DequeueNextRule()
        {
            lock (_queueLock)
            {
                return _shuffledRuleQueue.Count > 0 ? _shuffledRuleQueue.Dequeue() : 0;
            }
        }

        /// <summary>
        /// 남은 규칙 개수
        /// </summary>
        public int RemainingRuleCount
        {
            get
            {
                lock (_queueLock)
                {
                    return _shuffledRuleQueue.Count;
                }
            }
        }
    }

    /// <summary>
    /// MatchingId별로 Area 규칙 상태를 관리하는 매니저
    /// 각 매칭 인스턴스는 독립적인 규칙 세트를 가짐
    /// </summary>
    public class AreaRuleManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingAreaRuleState> _matchingStates = new();
        private CorridorRuleManager? _corridorRuleManager;

        public void Initialize(Action<string>? logAction = null, CorridorRuleManager? corridorRuleManager = null)
        {
            _logAction = logAction;
            _corridorRuleManager = corridorRuleManager;
            _matchingStates.Clear();
            _logAction?.Invoke("AreaRuleManager: Initialized");
        }

        /// <summary>
        /// 매칭 인스턴스의 규칙 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingAreaRuleState GetOrCreateMatchingState(long matchingId, Random? random = null)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                // CorridorRuleManager에서 이 매칭의 복도 규칙 ID 가져오기
                var corridorRuleId = _corridorRuleManager?.GetActiveRuleId(matchingId) ?? 0;

                var state = new MatchingAreaRuleState(corridorRuleId, random);
                _logAction?.Invoke($"AreaRuleManager: Created rule state for MatchingId={id}, CorridorRuleId={corridorRuleId}");

                // 선택된 규칙 로그
                foreach (var (area, rules) in state.GetAllRules())
                {
                    _logAction?.Invoke($"  Area {area}: [{string.Join(", ", rules)}]");
                }

                return state;
            });
        }

        /// <summary>
        /// 특정 Area의 규칙 목록 가져오기
        /// </summary>
        public List<int> GetRulesForArea(long matchingId, AreaType areaType)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.GetRulesForArea(areaType);
        }

        /// <summary>
        /// 모든 Area의 규칙 가져오기
        /// </summary>
        public Dictionary<AreaType, List<int>> GetAllRules(long matchingId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.GetAllRules();
        }

        /// <summary>
        /// 쪽지 아이템 사용 시 다음 규칙 ID를 가져옴
        /// 모든 플레이어가 같은 순서로 규칙을 받음
        /// </summary>
        /// <returns>규칙 ID, 큐가 비어있으면 0</returns>
        public int DequeueNextRule(long matchingId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            var ruleId = state.DequeueNextRule();
            _logAction?.Invoke($"AreaRuleManager: MatchingId={matchingId} dequeued RuleId={ruleId}, remaining={state.RemainingRuleCount}");
            return ruleId;
        }

        /// <summary>
        /// 매칭 종료 시 해당 매칭의 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingStates.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"AreaRuleManager: Removed state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingStates.Clear();
            _logAction?.Invoke("AreaRuleManager: All matching states cleared");
        }
    }
}
