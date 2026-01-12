using System.Collections.Concurrent;
using network.common.data;

namespace game_server.services
{
    /// <summary>
    /// 단일 매칭 인스턴스의 아이템 풀 상태
    /// 각 상호작용 대상(interactId)별로 독립적인 풀 상태를 관리
    /// </summary>
    public class MatchingPoolState
    {
        // (interactId, poolId) -> 셔플된 아이템 리스트
        private readonly Dictionary<(int interactId, int poolId), List<int>> _shuffledPools = new();
        // (interactId, poolId) -> 다음에 제공할 인덱스
        private readonly Dictionary<(int interactId, int poolId), int> _poolIndices = new();
        private readonly object _poolLock = new();
        private readonly long _matchingId;

        public MatchingPoolState(long matchingId)
        {
            _matchingId = matchingId;
        }

        private static void ShuffleList<T>(List<T> list, Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// 특정 상호작용 대상의 풀 초기화 (최초 접근 시)
        /// </summary>
        private void EnsurePoolInitialized(int interactId, int poolId)
        {
            var key = (interactId, poolId);
            if (_shuffledPools.ContainsKey(key))
                return;

            var originalPool = GameInteractableData.GetItemPool(poolId);
            if (originalPool.Count > 0)
            {
                // interactId와 matchingId를 조합한 시드로 셔플
                var random = new Random((int)((_matchingId * 1000000L + interactId) % int.MaxValue));
                var shuffled = new List<int>(originalPool);
                ShuffleList(shuffled, random);
                _shuffledPools[key] = shuffled;
                _poolIndices[key] = 0;
            }
        }

        /// <summary>
        /// 특정 상호작용 대상의 풀에서 다음 아이템 가져오기
        /// 모든 아이템이 소진되면 null 반환
        /// </summary>
        public int? GetNextItem(int interactId, int poolId)
        {
            lock (_poolLock)
            {
                EnsurePoolInitialized(interactId, poolId);

                var key = (interactId, poolId);
                if (!_shuffledPools.TryGetValue(key, out var pool))
                    return null;

                if (!_poolIndices.TryGetValue(key, out var index))
                    return null;

                if (index >= pool.Count)
                    return null; // 풀 소진

                _poolIndices[key] = index + 1;
                return pool[index];
            }
        }

        /// <summary>
        /// 특정 상호작용 대상의 풀에 남은 아이템 개수
        /// </summary>
        public int GetRemainingCount(int interactId, int poolId)
        {
            lock (_poolLock)
            {
                var key = (interactId, poolId);
                if (!_shuffledPools.TryGetValue(key, out var pool))
                    return 0;

                if (!_poolIndices.TryGetValue(key, out var index))
                    return 0;

                return pool.Count - index;
            }
        }

        /// <summary>
        /// 특정 상호작용 대상의 풀이 존재하는지 확인
        /// </summary>
        public bool HasPool(int interactId, int poolId)
        {
            lock (_poolLock)
            {
                EnsurePoolInitialized(interactId, poolId);
                return _shuffledPools.ContainsKey((interactId, poolId));
            }
        }
    }

    /// <summary>
    /// MatchingId별로 아이템 풀 상태를 관리하는 매니저
    /// </summary>
    public class ItemPoolManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingPoolState> _matchingPools = new();

        public void Initialize(Action<string>? logAction = null)
        {
            _logAction = logAction;
            _matchingPools.Clear();
            _logAction?.Invoke("ItemPoolManager: Initialized");
        }

        /// <summary>
        /// 매칭 인스턴스의 풀 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingPoolState GetOrCreatePoolState(long matchingId)
        {
            return _matchingPools.GetOrAdd(matchingId, id =>
            {
                _logAction?.Invoke($"ItemPoolManager: Creating shuffled pools for MatchingId={id}");
                return new MatchingPoolState(id);
            });
        }

        /// <summary>
        /// 특정 상호작용 대상의 풀에서 다음 아이템 가져오기
        /// </summary>
        public int? GetNextItemFromPool(long matchingId, int interactId, int poolId)
        {
            var state = GetOrCreatePoolState(matchingId);
            var item = state.GetNextItem(interactId, poolId);

            if (item.HasValue)
            {
                _logAction?.Invoke($"ItemPoolManager: MatchingId={matchingId} InteractId={interactId} got item {item.Value} from pool {poolId}, remaining={state.GetRemainingCount(interactId, poolId)}");
            }
            else
            {
                _logAction?.Invoke($"ItemPoolManager: MatchingId={matchingId} InteractId={interactId} pool {poolId} exhausted or not found");
            }

            return item;
        }

        /// <summary>
        /// 매칭 종료 시 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingPools.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"ItemPoolManager: Removed pool state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingPools.Clear();
            _logAction?.Invoke("ItemPoolManager: All pool states cleared");
        }
    }
}
