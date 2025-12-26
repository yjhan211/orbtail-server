using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services
{
    /// <summary>
    /// 복도 규칙 타입 (현재 미사용 - CSV 기반으로 전환됨)
    /// </summary>
    public enum CorridorRuleType
    {
        None = 0
    }

    /// <summary>
    /// 플레이어별 복도 상태 추적
    /// </summary>
    public class PlayerCorridorState
    {
        public bool IsInCorridor { get; set; }
        public DateTime LastMoveTime { get; set; } = DateTime.UtcNow;
        public Vector3f? LastPosition { get; set; }

        public void Reset()
        {
            IsInCorridor = false;
            LastPosition = null;
        }
    }

    /// <summary>
    /// 복도 규칙 위반 결과
    /// </summary>
    public class CorridorViolationResult
    {
        public bool IsViolation { get; set; }
        public CorridorRuleType ViolatedRule { get; set; }
        public int CorruptionDelta { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// 매칭 인스턴스별 복도 규칙 상태 관리
    /// 현재는 CSV 기반 규칙으로 전환되어 위반 체크 비활성화됨
    /// </summary>
    public class MatchingCorridorRuleState
    {
        public CorridorRuleType ActiveRule => CorridorRuleType.None;
        private readonly ConcurrentDictionary<long, PlayerCorridorState> _playerStates = new();

        public List<(long PlayerId, CorridorViolationResult Result)> CheckAllPlayersForStopping()
        {
            // CSV 기반 규칙으로 전환됨 - 위반 체크 비활성화
            return new List<(long, CorridorViolationResult)>();
        }

        public PlayerCorridorState GetOrCreatePlayerState(long playerId)
        {
            return _playerStates.GetOrAdd(playerId, _ => new PlayerCorridorState());
        }

        public void RemovePlayerState(long playerId)
        {
            _playerStates.TryRemove(playerId, out _);
        }

        /// <summary>
        /// 플레이어 이동 시 복도 상태 업데이트 (위반 체크 비활성화됨)
        /// </summary>
        public CorridorViolationResult CheckMove(long playerId, Vector3f position, Vector3f velocity, AreaType currentArea)
        {
            var result = new CorridorViolationResult();
            var state = GetOrCreatePlayerState(playerId);

            // 복도 진입/퇴장 처리
            var wasInCorridor = state.IsInCorridor;
            var isNowInCorridor = currentArea == AreaType.Corridor;

            if (!wasInCorridor && isNowInCorridor)
            {
                state.IsInCorridor = true;
                state.LastPosition = position;
                state.LastMoveTime = DateTime.UtcNow;
            }
            else if (wasInCorridor && !isNowInCorridor)
            {
                state.Reset();
            }

            // CSV 기반 규칙으로 전환됨 - 위반 체크 비활성화
            return result;
        }
    }

    /// <summary>
    /// 복도 규칙 매니저 - MatchingId별로 복도 상태 관리
    /// 현재는 CSV 기반 규칙으로 전환되어 위반 체크 비활성화됨
    /// </summary>
    public class CorridorRuleManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingCorridorRuleState> _matchingStates = new();

        public void Initialize(Action<string>? logAction = null, Action<long, long, int>? onStopViolationCallback = null)
        {
            _logAction = logAction;
            _matchingStates.Clear();
            _logAction?.Invoke("CorridorRuleManager: Initialized (CSV-based rules - violation check disabled)");
        }

        /// <summary>
        /// 매칭 인스턴스의 복도 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingCorridorRuleState GetOrCreateMatchingState(long matchingId)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                var state = new MatchingCorridorRuleState();
                _logAction?.Invoke($"CorridorRuleManager: Created state for MatchingId={id}");
                return state;
            });
        }

        /// <summary>
        /// 플레이어 이동 시 복도 상태 업데이트
        /// </summary>
        public CorridorViolationResult CheckPlayerMove(long matchingId, long playerId, Vector3f position, Vector3f velocity, AreaType currentArea)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.CheckMove(playerId, position, velocity, currentArea);
        }

        /// <summary>
        /// 현재 매칭의 활성 복도 규칙 가져오기 (항상 None 반환)
        /// </summary>
        public CorridorRuleType GetActiveRule(long matchingId)
        {
            return CorridorRuleType.None;
        }

        /// <summary>
        /// 현재 매칭의 활성 복도 규칙 ID 가져오기 (항상 0 반환)
        /// </summary>
        public int GetActiveRuleId(long matchingId)
        {
            return 0;
        }

        /// <summary>
        /// 매칭 종료 시 해당 매칭의 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingStates.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"CorridorRuleManager: Removed state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 플레이어 세션 종료 시 해당 플레이어 상태 정리
        /// </summary>
        public void RemovePlayerState(long matchingId, long playerId)
        {
            if (_matchingStates.TryGetValue(matchingId, out var state))
            {
                state.RemovePlayerState(playerId);
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingStates.Clear();
            _logAction?.Invoke("CorridorRuleManager: All matching states cleared");
        }
    }
}
