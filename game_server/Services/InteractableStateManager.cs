using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services
{
    /// <summary>
    /// 게임 세션 동안 각 Area의 Interactable 선택지 탐색 상태를 관리
    /// </summary>
    public class InteractableStateManager
    {
        // Key: (InteractId, Order), Value: InteractableActionState
        private readonly ConcurrentDictionary<(int, int), InteractableActionState> _actionStates = new();

        // Area별 오브젝트 ID 목록
        private readonly ConcurrentDictionary<AreaType, List<int>> _areaObjects = new();

        /// <summary>
        /// 게임 시작 시 모든 Interactable 선택지 상태 초기화
        /// </summary>
        public void Initialize()
        {
            _actionStates.Clear();
            _areaObjects.Clear();

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

                foreach (var action in interactable.Actions)
                {
                    var key = (interactable.Id, action.Order);
                    _actionStates[key] = new InteractableActionState
                    {
                        Order = action.Order,
                        IsExplored = false,
                        ExploredBy = 0
                    };
                }
            }
        }

        /// <summary>
        /// 특정 Area의 오브젝트별 선택지 상태 반환 (그룹핑된 형태)
        /// </summary>
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
                    var key = (interactId, action.Order);
                    if (_actionStates.TryGetValue(key, out var actionState))
                    {
                        objectState.Actions.Add(actionState);
                    }
                }

                result.Add(objectState);
            }

            return result;
        }

        /// <summary>
        /// 특정 선택지 탐색 처리
        /// </summary>
        /// <returns>성공 여부 (이미 탐색된 경우 false)</returns>
        public bool TryExplore(int interactId, int order, long playerId, out InteractableActionState? state)
        {
            state = null;
            var key = (interactId, order);

            if (!_actionStates.TryGetValue(key, out var currentState))
            {
                return false;
            }

            // 이미 탐색된 경우
            if (currentState.IsExplored)
            {
                state = currentState;
                return false;
            }

            // 탐색 처리
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

        /// <summary>
        /// 특정 선택지 상태 조회
        /// </summary>
        public InteractableActionState? GetState(int interactId, int order)
        {
            var key = (interactId, order);
            return _actionStates.TryGetValue(key, out var state) ? state : null;
        }

        /// <summary>
        /// 게임 종료 시 상태 초기화
        /// </summary>
        public void Reset()
        {
            _actionStates.Clear();
            _areaObjects.Clear();
        }
    }
}
