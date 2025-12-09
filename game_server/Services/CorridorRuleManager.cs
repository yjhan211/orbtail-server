using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services
{
    /// <summary>
    /// 복도 규칙 타입 (area_rule.csv의 ID와 매핑)
    /// </summary>
    public enum CorridorRuleType
    {
        None = 0,
        NoRunning = 12001,      // 복도에서는 절대 뛰지 마십시오
        NoStopping = 12002,     // 복도에서는 절대 멈춰 서지 마십시오
        TenStepsMax = 12003,    // 복도에서는 한 번에 열 걸음 이상을 내딛지 마십시오
    }

    /// <summary>
    /// 플레이어별 복도 상태 추적
    /// </summary>
    public class PlayerCorridorState
    {
        public bool IsInCorridor { get; set; }
        public DateTime? CorridorEntryTime { get; set; }
        public Vector3f? CorridorEntryPosition { get; set; }
        public float AccumulatedDistance { get; set; }
        public DateTime LastMoveTime { get; set; } = DateTime.UtcNow;
        public Vector3f? LastPosition { get; set; }

        // 위반 쿨다운 (너무 자주 위반 판정 방지)
        public DateTime LastViolationTime { get; set; } = DateTime.MinValue;

        public void Reset()
        {
            IsInCorridor = false;
            CorridorEntryTime = null;
            CorridorEntryPosition = null;
            AccumulatedDistance = 0;
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
    /// </summary>
    public class MatchingCorridorRuleState
    {
        public CorridorRuleType ActiveRule { get; }
        private readonly ConcurrentDictionary<long, PlayerCorridorState> _playerStates = new();

        // 규칙별 설정값
        private const float WalkSpeedThreshold = 3.5f;  // 걷기 속도 임계값 (이 이상이면 뛰기)
        private const float StopDurationSeconds = 2.0f; // 정지 판정 시간 (초)
        private const float StepDistance = 0.3f;        // 한 걸음 거리 (units) - 3 units 이동 시 위반
        private const int MaxSteps = 10;                // 최대 걸음 수
        private const float ViolationCooldownSeconds = 3.0f; // 위반 쿨다운 (초)
        private const int CorruptionPerViolation = 5;   // 위반당 정신오염도 증가량

        public MatchingCorridorRuleState(CorridorRuleType rule)
        {
            ActiveRule = rule;
        }

        /// <summary>
        /// 복도에 있는 모든 플레이어의 정지 상태를 체크 (타이머에서 호출)
        /// </summary>
        public List<(long PlayerId, CorridorViolationResult Result)> CheckAllPlayersForStopping()
        {
            var violations = new List<(long, CorridorViolationResult)>();

            if (ActiveRule != CorridorRuleType.NoStopping)
                return violations;

            var now = DateTime.UtcNow;

            foreach (var (playerId, state) in _playerStates)
            {
                if (!state.IsInCorridor) continue;

                // 쿨다운 체크
                if ((now - state.LastViolationTime).TotalSeconds < ViolationCooldownSeconds)
                    continue;

                // 마지막 이동 이후 정지 시간 체크
                var stopDuration = (now - state.LastMoveTime).TotalSeconds;
                if (stopDuration >= StopDurationSeconds)
                {
                    var result = new CorridorViolationResult
                    {
                        IsViolation = true,
                        ViolatedRule = CorridorRuleType.NoStopping,
                        CorruptionDelta = CorruptionPerViolation,
                        Message = "복도에서 멈춰 섰습니다"
                    };

                    state.LastViolationTime = now;
                    violations.Add((playerId, result));
                }
            }

            return violations;
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
        /// 플레이어 이동 시 복도 규칙 체크
        /// </summary>
        public CorridorViolationResult CheckMove(long playerId, Vector3f position, Vector3f velocity, AreaType currentArea)
        {
            var result = new CorridorViolationResult();
            var state = GetOrCreatePlayerState(playerId);
            var now = DateTime.UtcNow;

            // 복도 진입/퇴장 처리
            var wasInCorridor = state.IsInCorridor;
            var isNowInCorridor = currentArea == AreaType.Corridor;

            if (!wasInCorridor && isNowInCorridor)
            {
                // 복도 진입
                state.IsInCorridor = true;
                state.CorridorEntryTime = now;
                state.CorridorEntryPosition = position;
                state.AccumulatedDistance = 0;
                state.LastPosition = position;
                state.LastMoveTime = now;
            }
            else if (wasInCorridor && !isNowInCorridor)
            {
                // 복도 퇴장
                state.Reset();
                return result; // 복도 밖이면 체크 안함
            }

            // 복도 안이 아니면 체크 안함
            if (!isNowInCorridor)
            {
                return result;
            }

            // 쿨다운 체크
            if ((now - state.LastViolationTime).TotalSeconds < ViolationCooldownSeconds)
            {
                state.LastPosition = position;
                state.LastMoveTime = now;
                return result;
            }

            // 규칙별 체크
            switch (ActiveRule)
            {
                case CorridorRuleType.NoRunning:
                    result = CheckNoRunning(state, velocity, now);
                    break;

                case CorridorRuleType.NoStopping:
                    result = CheckNoStopping(state, velocity, now);
                    break;

                case CorridorRuleType.TenStepsMax:
                    result = CheckTenStepsMax(state, position, now);
                    break;
            }

            // 상태 업데이트
            state.LastPosition = position;

            // NoStopping 규칙: 움직일 때만 LastMoveTime 갱신 (정지 시간 측정을 위해)
            var speed = velocity.Magnitude();
            if (ActiveRule != CorridorRuleType.NoStopping || speed >= 0.1f)
            {
                state.LastMoveTime = now;
            }

            if (result.IsViolation)
            {
                state.LastViolationTime = now;

                // 10걸음 규칙은 위반 후 거리 리셋
                if (ActiveRule == CorridorRuleType.TenStepsMax)
                {
                    state.AccumulatedDistance = 0;
                    state.CorridorEntryPosition = position;
                }
            }

            return result;
        }

        private CorridorViolationResult CheckNoRunning(PlayerCorridorState state, Vector3f velocity, DateTime now)
        {
            var result = new CorridorViolationResult();
            var speed = velocity.Magnitude();

            if (speed > WalkSpeedThreshold)
            {
                result.IsViolation = true;
                result.ViolatedRule = CorridorRuleType.NoRunning;
                result.CorruptionDelta = CorruptionPerViolation;
                result.Message = "복도에서 뛰었습니다";
            }

            return result;
        }

        private CorridorViolationResult CheckNoStopping(PlayerCorridorState state, Vector3f velocity, DateTime now)
        {
            var result = new CorridorViolationResult();
            var speed = velocity.Magnitude();

            // 정지 상태 (속도가 매우 낮음)
            if (speed < 0.1f)
            {
                var stopDuration = (now - state.LastMoveTime).TotalSeconds;

                // 일정 시간 이상 정지하면 위반
                if (stopDuration >= StopDurationSeconds)
                {
                    result.IsViolation = true;
                    result.ViolatedRule = CorridorRuleType.NoStopping;
                    result.CorruptionDelta = CorruptionPerViolation;
                    result.Message = "복도에서 멈춰 섰습니다";
                }
            }
            else
            {
                // 움직이면 타이머 리셋 (LastMoveTime은 외부에서 업데이트)
            }

            return result;
        }

        private CorridorViolationResult CheckTenStepsMax(PlayerCorridorState state, Vector3f position, DateTime now)
        {
            var result = new CorridorViolationResult();

            if (state.LastPosition != null)
            {
                var delta = position - state.LastPosition;
                var distance = delta.Magnitude();
                state.AccumulatedDistance += distance;

                var steps = state.AccumulatedDistance / StepDistance;

                if (steps > MaxSteps)
                {
                    result.IsViolation = true;
                    result.ViolatedRule = CorridorRuleType.TenStepsMax;
                    result.CorruptionDelta = CorruptionPerViolation;
                    result.Message = "복도에서 열 걸음 이상 걸었습니다";
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 복도 규칙 매니저 - MatchingId별로 복도 규칙 상태 관리
    /// </summary>
    public class CorridorRuleManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingCorridorRuleState> _matchingStates = new();
        private readonly Random _random = new();
        private Timer? _stopCheckTimer;

        // 정지 체크 콜백: (matchingId, playerId, corruptionDelta) => 정신오염도 증가 처리
        private Action<long, long, int>? _onStopViolationCallback;

        // 복도 규칙 ID 목록
        private static readonly CorridorRuleType[] CorridorRules =
        {
            CorridorRuleType.NoRunning,
            CorridorRuleType.NoStopping,
            CorridorRuleType.TenStepsMax,
        };

        private const int StopCheckIntervalMs = 500; // 0.5초마다 체크

        public void Initialize(Action<string>? logAction = null, Action<long, long, int>? onStopViolationCallback = null)
        {
            _logAction = logAction;
            _onStopViolationCallback = onStopViolationCallback;
            _matchingStates.Clear();

            // 정지 체크 타이머 시작
            _stopCheckTimer?.Dispose();
            _stopCheckTimer = new Timer(CheckAllMatchingsForStopping, null, StopCheckIntervalMs, StopCheckIntervalMs);

            _logAction?.Invoke("CorridorRuleManager: Initialized with stop check timer");
        }

        /// <summary>
        /// 모든 매칭의 정지 위반 체크 (타이머에서 호출)
        /// </summary>
        private void CheckAllMatchingsForStopping(object? state)
        {
            foreach (var (matchingId, matchingState) in _matchingStates)
            {
                var violations = matchingState.CheckAllPlayersForStopping();

                foreach (var (playerId, result) in violations)
                {
                    _logAction?.Invoke($"CorridorRuleManager: Player {playerId} violated rule {result.ViolatedRule}: {result.Message}");
                    _onStopViolationCallback?.Invoke(matchingId, playerId, result.CorruptionDelta);
                }
            }
        }

        /// <summary>
        /// 매칭 인스턴스의 복도 규칙 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingCorridorRuleState GetOrCreateMatchingState(long matchingId)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                // 랜덤하게 복도 규칙 선택
                var selectedRule = CorridorRules[_random.Next(CorridorRules.Length)];
                var state = new MatchingCorridorRuleState(selectedRule);

                _logAction?.Invoke($"CorridorRuleManager: Created state for MatchingId={id}, ActiveRule={selectedRule} ({(int)selectedRule})");

                return state;
            });
        }

        /// <summary>
        /// 플레이어 이동 시 복도 규칙 체크
        /// </summary>
        public CorridorViolationResult CheckPlayerMove(long matchingId, long playerId, Vector3f position, Vector3f velocity, AreaType currentArea)
        {
            var state = GetOrCreateMatchingState(matchingId);
            var result = state.CheckMove(playerId, position, velocity, currentArea);

            if (result.IsViolation)
            {
                _logAction?.Invoke($"CorridorRuleManager: Player {playerId} violated rule {result.ViolatedRule}: {result.Message}");
            }

            return result;
        }

        /// <summary>
        /// 현재 매칭의 활성 복도 규칙 가져오기
        /// </summary>
        public CorridorRuleType GetActiveRule(long matchingId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.ActiveRule;
        }

        /// <summary>
        /// 현재 매칭의 활성 복도 규칙 ID 가져오기
        /// </summary>
        public int GetActiveRuleId(long matchingId)
        {
            return (int)GetActiveRule(matchingId);
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
