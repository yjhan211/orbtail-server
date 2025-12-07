using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services
{
    /// <summary>
    /// 단일 매칭 인스턴스의 Interactable 상태를 관리
    /// </summary>
    public class MatchingInteractableState
    {
        // Key: (InteractId, Order), Value: InteractableActionState
        private readonly ConcurrentDictionary<(int, int), InteractableActionState> _actionStates = new();

        // Area별 오브젝트 ID 목록 (정적 데이터 캐시)
        private readonly ConcurrentDictionary<AreaType, List<int>> _areaObjects = new();

        // Key: InteractId, Value: 셔플된 보상 배열 (액션 순서대로 매핑, 실제 아이템 ID로 변환됨)
        private readonly ConcurrentDictionary<int, List<int>> _shuffledRewards = new();

        private readonly Random _random;

        public MatchingInteractableState(long matchingId)
        {
            _random = new Random((int)(matchingId % int.MaxValue));
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

                if (!objectIds.Contains(interactable.Id))
                {
                    objectIds.Add(interactable.Id);
                }

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

                // Reward Pool 가져와서 셔플 후 실제 아이템 ID로 변환
                var rewardPool = GameInteractableData.GetRewardPool(interactable.RewardPoolId);
                if (rewardPool.Count > 0)
                {
                    // (RewardType, RewardId) 튜플 리스트로 복사
                    var rewardInfos = rewardPool
                        .Select(r => (r.RewardType, r.RewardId))
                        .ToList();

                    // 셔플
                    Shuffle(rewardInfos);

                    // 랜덤 타입을 실제 아이템 ID로 변환
                    var resolvedRewards = rewardInfos
                        .Select(info => ResolveRewardId(info.RewardType, info.RewardId))
                        .ToList();

                    _shuffledRewards[interactable.Id] = resolvedRewards;
                }
            }
        }

        /// <summary>
        /// RewardType에 따라 실제 아이템 ID 결정
        /// </summary>
        private int ResolveRewardId(RewardType rewardType, int rewardId)
        {
            switch (rewardType)
            {
                case RewardType.ITEM:
                    return rewardId;

                case RewardType.CONDITION_RANDOM:
                    var conditionItem = GameItemData.GetRandomConsumableByBuffSubType(BuffSubType.CONDITION_ADD, _random);
                    return conditionItem?.Id ?? 0;

                case RewardType.CORRUPTION_RANDOM:
                    var corruptionItem = GameItemData.GetRandomConsumableByBuffSubType(BuffSubType.CORRUPTION_DOWN, _random);
                    return corruptionItem?.Id ?? 0;

                case RewardType.NONE:
                default:
                    return 0;
            }
        }

        private void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// 특정 오브젝트의 액션 인덱스에 해당하는 보상 ID 반환 (0이면 보상 없음)
        /// </summary>
        public int GetRewardForAction(int interactId, int actionIndex)
        {
            if (!_shuffledRewards.TryGetValue(interactId, out var rewards))
                return 0;

            // actionIndex는 1-based (ActionId), 배열은 0-based
            var index = actionIndex - 1;
            if (index < 0 || index >= rewards.Count)
                return 0;

            return rewards[index];
        }

        public List<InteractableObjectState> GetAreaObjectStates(AreaType areaType)
        {
            if (!_areaObjects.TryGetValue(areaType, out var objectIds))
            {
                return new List<InteractableObjectState>();
            }

            var result = new List<InteractableObjectState>();
            foreach (var interactId in objectIds)
            {
                var interactable = GameInteractableData.Get(interactId);
                if (interactable == null) continue;

                var objectState = new InteractableObjectState
                {
                    InteractId = interactId,
                    Actions = new List<InteractableActionState>()
                };

                foreach (var action in interactable.Actions)
                {
                    var key = (interactId, action.ActionId);
                    if (_actionStates.TryGetValue(key, out var actionState))
                    {
                        objectState.Actions.Add(actionState);
                    }
                }

                result.Add(objectState);
            }

            return result;
        }

        public bool TryExplore(int interactId, int order, long playerId, out InteractableActionState? state)
        {
            state = null;
            var key = (interactId, order);

            if (!_actionStates.TryGetValue(key, out var currentState))
            {
                return false;
            }

            if (currentState.IsExplored)
            {
                state = currentState;
                return false;
            }

            var newState = new InteractableActionState
            {
                Order = order,
                IsExplored = true,
                ExploredBy = playerId
            };

            _actionStates[key] = newState;
            state = newState;
            return true;
        }

        public InteractableActionState? GetState(int interactId, int order)
        {
            var key = (interactId, order);
            return _actionStates.TryGetValue(key, out var state) ? state : null;
        }
    }

    /// <summary>
    /// MatchingId별로 Interactable 상태를 관리하는 매니저
    /// 각 매칭 인스턴스는 독립적인 상태를 가짐
    /// </summary>
    public class InteractableStateManager
    {
        private Action<string>? _logAction;

        // Key: MatchingId, Value: 해당 매칭의 Interactable 상태
        private readonly ConcurrentDictionary<long, MatchingInteractableState> _matchingStates = new();

        public void Initialize(Action<string>? logAction = null)
        {
            _logAction = logAction;
            _matchingStates.Clear();

            var allInteractables = GameInteractableData.GetAll();
            _logAction?.Invoke($"InteractableStateManager: Loaded {allInteractables.Count} interactables from GameInteractableData (per-matching isolation enabled)");
        }

        /// <summary>
        /// 매칭 인스턴스의 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingInteractableState GetOrCreateMatchingState(long matchingId)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                _logAction?.Invoke($"InteractableStateManager: Creating new state for MatchingId={id}");
                return new MatchingInteractableState(id);
            });
        }

        /// <summary>
        /// 특정 Area의 오브젝트별 선택지 상태 반환
        /// </summary>
        public List<InteractableObjectState> GetAreaObjectStates(long matchingId, AreaType areaType)
        {
            var matchingState = GetOrCreateMatchingState(matchingId);
            return matchingState.GetAreaObjectStates(areaType);
        }

        /// <summary>
        /// 특정 선택지 탐색 처리
        /// </summary>
        public bool TryExplore(long matchingId, int interactId, int order, long playerId, out InteractableActionState? state)
        {
            var matchingState = GetOrCreateMatchingState(matchingId);
            return matchingState.TryExplore(interactId, order, playerId, out state);
        }

        /// <summary>
        /// 특정 선택지 상태 조회
        /// </summary>
        public InteractableActionState? GetState(long matchingId, int interactId, int order)
        {
            var matchingState = GetOrCreateMatchingState(matchingId);
            return matchingState.GetState(interactId, order);
        }

        /// <summary>
        /// 특정 오브젝트의 액션에 해당하는 셔플된 보상 ID 반환
        /// </summary>
        public int GetRewardForAction(long matchingId, int interactId, int actionId)
        {
            var matchingState = GetOrCreateMatchingState(matchingId);
            return matchingState.GetRewardForAction(interactId, actionId);
        }

        /// <summary>
        /// 매칭 종료 시 해당 매칭의 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingStates.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"InteractableStateManager: Removed state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingStates.Clear();
            _logAction?.Invoke("InteractableStateManager: All matching states cleared");
        }
    }
}
