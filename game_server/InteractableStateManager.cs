using System.Collections.Concurrent;
using network.common.data;
using network.common.data.models;

namespace game_server
{
    /// <summary>
    /// 게임 세션 동안 각 Zone의 Interactable 탐색 상태를 관리
    /// </summary>
    public class InteractableStateManager
    {
        // Key: ZoneId, Value: (InteractableId -> InteractableState)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, InteractableState>> _zoneStates = new();

        /// <summary>
        /// 게임 시작 시 모든 Zone의 Interactable 상태 초기화
        /// </summary>
        public void Initialize()
        {
            _zoneStates.Clear();

            var allInteractables = GameInteractableData.GetAll();
            foreach (var interactable in allInteractables)
            {
                if (!_zoneStates.TryGetValue(interactable.ZoneId, out var zoneDict))
                {
                    zoneDict = new ConcurrentDictionary<int, InteractableState>();
                    _zoneStates[interactable.ZoneId] = zoneDict;
                }

                zoneDict[interactable.Id] = new InteractableState
                {
                    Id = interactable.Id,
                    IsExplored = false,
                    ExploredBy = 0
                };
            }
        }

        /// <summary>
        /// 특정 Zone의 모든 Interactable 상태 반환
        /// </summary>
        public List<InteractableState> GetZoneStates(int zoneId)
        {
            if (!_zoneStates.TryGetValue(zoneId, out var zoneDict))
            {
                return new List<InteractableState>();
            }

            return zoneDict.Values.ToList();
        }

        /// <summary>
        /// 특정 Interactable 탐색 처리
        /// </summary>
        /// <returns>성공 여부 (이미 탐색된 경우 false)</returns>
        public bool TryExplore(int interactableId, long playerId, out InteractableState? state)
        {
            state = null;

            // interactableId에서 zoneId 추출 (7XXYYYZZZ 형식에서 XX 부분)
            var zoneId = (interactableId / 1000000) % 100;

            if (!_zoneStates.TryGetValue(zoneId, out var zoneDict))
            {
                return false;
            }

            if (!zoneDict.TryGetValue(interactableId, out var currentState))
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
            var newState = new InteractableState
            {
                Id = interactableId,
                IsExplored = true,
                ExploredBy = playerId
            };

            zoneDict[interactableId] = newState;
            state = newState;
            return true;
        }

        /// <summary>
        /// 특정 Interactable 상태 조회
        /// </summary>
        public InteractableState? GetState(int interactableId)
        {
            var zoneId = (interactableId / 1000000) % 100;

            if (!_zoneStates.TryGetValue(zoneId, out var zoneDict))
            {
                return null;
            }

            return zoneDict.TryGetValue(interactableId, out var state) ? state : null;
        }

        /// <summary>
        /// 게임 종료 시 상태 초기화
        /// </summary>
        public void Reset()
        {
            _zoneStates.Clear();
        }
    }
}
