using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

// 복도 종소리 설정
public static class CorridorBellConfig
{
    public const int MinIntervalSeconds = 10; // 최소 간격
    public const int MaxIntervalSeconds = 20; // 최대 간격
    public const int MinDurationSeconds = 3; // 최소 지속 시간
    public const int MaxDurationSeconds = 6; // 최대 지속 시간
    public const int MovementPenaltyCorruption = 5; // 종소리 중 이동 시 정신오염 페널티 (규칙 1)
    public const int StopPenaltyCorruption = 5; // 정지 시 정신오염 페널티 (규칙 6)
    public const double StopThresholdSeconds = 1.0; // 정지 판정 시간 (초)
    public const double PenaltyCooldownSeconds = 1.0; // 페널티 쿨다운 (초)
    public static int GameDurationMinutes => Config.GAME_DURATION_MINUTES; // 게임 시간 (Config에서 참조)
}

/// <summary>
///     복도 규칙 타입 (현재 미사용 - CSV 기반으로 전환됨)
/// </summary>
public enum CorridorRuleType
{
    None = 0
}

/// <summary>
///     플레이어별 복도 상태 추적
/// </summary>
public class PlayerCorridorState
{
    public bool IsInCorridor { get; set; }
    public DateTime LastPacketTime { get; set; } = DateTime.UtcNow; // 마지막 패킷 수신 시간
    public DateTime LastPenaltyTime { get; set; } = DateTime.MinValue; // 마지막 페널티 적용 시간
    public DateTime? StopStartTime { get; set; } // 정지 시작 시간 (규칙 6용)

    public void Reset()
    {
        IsInCorridor = false;
        StopStartTime = null;
    }
}

/// <summary>
///     복도 규칙 위반 결과
/// </summary>
public class CorridorViolationResult
{
    public bool IsViolation { get; set; }
    public CorridorRuleType ViolatedRule { get; set; }
    public int CorruptionDelta { get; set; }
    public string? Message { get; set; }
}

/// <summary>
///     매칭 인스턴스별 복도 규칙 상태 관리
///     현재는 CSV 기반 규칙으로 전환되어 위반 체크 비활성화됨
/// </summary>
public class MatchingCorridorRuleState
{
    private static readonly Random Random = new();
    private readonly IReadOnlyList<BellEvent> _bellSchedule = GenerateBellSchedule();
    private readonly long _gameStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // 게임 시작 시간 (Unix ms)
    private readonly ConcurrentDictionary<long, PlayerCorridorState> _playerStates = new();

    /// <summary>
    ///     게임 시작 시 종소리 스케줄 생성 (상대 시간 기반)
    /// </summary>
    private static List<BellEvent> GenerateBellSchedule()
    {
        var schedule = new List<BellEvent>();

        int gameDurationSec = CorridorBellConfig.GameDurationMinutes * 60;
        int currentOffsetSec = 0;

        while (currentOffsetSec < gameDurationSec)
        {
            // 랜덤 간격 후 다음 종소리
            int intervalSeconds = Random.Next(
                CorridorBellConfig.MinIntervalSeconds,
                CorridorBellConfig.MaxIntervalSeconds + 1);
            currentOffsetSec += intervalSeconds;

            if (currentOffsetSec >= gameDurationSec) break;

            int durationSeconds = Random.Next(
                CorridorBellConfig.MinDurationSeconds,
                CorridorBellConfig.MaxDurationSeconds + 1);

            schedule.Add(new BellEvent { StartOffsetSec = currentOffsetSec, DurationSec = durationSeconds });
        }

        return schedule;
    }

    /// <summary>
    ///     현재 종소리가 울리고 있는지 확인
    /// </summary>
    private bool IsBellRinging(out BellEvent? currentBell)
    {
        currentBell = null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int elapsedSec = (int)((now - _gameStartTimeMs) / 1000);

        foreach (var bell in _bellSchedule)
        {
            int bellStartSec = bell.StartOffsetSec;
            int bellEndSec = bell.StartOffsetSec + bell.DurationSec;
            if (elapsedSec >= bellStartSec && elapsedSec <= bellEndSec)
            {
                currentBell = bell;
                return true;
            }
        }

        return false;
    }

    public List<(long PlayerId, CorridorViolationResult Result)> CheckAllPlayersForStopping()
    {
        // CSV 기반 규칙으로 전환됨 - 위반 체크 비활성화
        return new List<(long, CorridorViolationResult)>();
    }

    private PlayerCorridorState GetOrCreatePlayerState(long playerId)
    {
        return _playerStates.GetOrAdd(playerId, _ => new PlayerCorridorState());
    }

    public void RemovePlayerState(long playerId)
    {
        _playerStates.TryRemove(playerId, out _);
    }

    /// <summary>
    ///     플레이어 이동 시 복도 상태 업데이트 및 위반 체크
    /// </summary>
    public CorridorViolationResult CheckMove(long playerId, Vector3f position, Vector3f velocity, AreaType currentArea,
        int corridorRuleId)
    {
        var result = new CorridorViolationResult();
        var state = GetOrCreatePlayerState(playerId);

        // IsBellRinging은 불변 _bellSchedule만 읽으므로 lock 밖에서 호출
        bool isBellRinging = IsBellRinging(out var currentBell);

        lock (state)
        {
            // 복도 진입/퇴장 처리
            bool wasInCorridor = state.IsInCorridor;
            bool isNowInCorridor = currentArea.IsCorridor();

            if (!wasInCorridor && isNowInCorridor)
            {
                state.IsInCorridor = true;
                state.StopStartTime = null;
            }
            else if (wasInCorridor && !isNowInCorridor)
            {
                state.Reset();
            }

            if (!isNowInCorridor) return result;

            bool isMoving = velocity.Magnitude() > 0.1f;
            var now = DateTime.UtcNow;
            state.LastPacketTime = now; // 패킷 수신 시간 업데이트

            // 복도 규칙 1: 종소리 울릴 때 복도에서 움직이면 정신오염 페널티
            if (corridorRuleId == 1 && isBellRinging)
            {
                if (isMoving)
                {
                    double timeSinceLastPenalty = (now - state.LastPenaltyTime).TotalSeconds;
                    if (timeSinceLastPenalty >= CorridorBellConfig.PenaltyCooldownSeconds)
                    {
                        state.LastPenaltyTime = now;
                        result.IsViolation = true;
                        result.CorruptionDelta = CorridorBellConfig.MovementPenaltyCorruption;
                        result.Message =
                            $"종소리가 울리는 동안 복도에서 움직임 (Bell: {currentBell?.StartOffsetSec}s +{currentBell?.DurationSec}s)";
                    }
                }
            }
            // 복도 규칙 6: 복도에서 정지하면 정신오염 페널티
            else if (corridorRuleId == 6)
            {
                if (isMoving)
                {
                    // 움직이면 정지 타이머 리셋
                    state.StopStartTime = null;
                }
                else
                {
                    // 정지 상태
                    if (!state.StopStartTime.HasValue)
                    {
                        // 정지 시작
                        state.StopStartTime = now;
                    }
                    else
                    {
                        // 정지 지속 시간 체크
                        double stopDuration = (now - state.StopStartTime.Value).TotalSeconds;
                        if (stopDuration >= CorridorBellConfig.StopThresholdSeconds)
                        {
                            double timeSinceLastPenalty = (now - state.LastPenaltyTime).TotalSeconds;
                            if (timeSinceLastPenalty >= CorridorBellConfig.PenaltyCooldownSeconds)
                            {
                                state.LastPenaltyTime = now;
                                state.StopStartTime = now; // 페널티 적용 후 리셋
                                result.IsViolation = true;
                                result.CorruptionDelta = CorridorBellConfig.StopPenaltyCorruption;
                                result.Message = "복도에서 정지";
                            }
                        }
                    }
                }
            }

            return result;
        }
    }

    /// <summary>
    ///     타이머 기반 정지 체크 (패킷이 오지 않는 플레이어들 대상)
    /// </summary>
    public List<(long PlayerId, CorridorViolationResult Result)> CheckStoppedPlayersForRule6()
    {
        var results = new List<(long, CorridorViolationResult)>();
        var now = DateTime.UtcNow;

        foreach (var kvp in _playerStates)
        {
            long playerId = kvp.Key;
            var state = kvp.Value;

            if (!state.IsInCorridor) continue;

            // 패킷이 일정 시간 이상 안 왔으면 정지로 판정
            double timeSinceLastPacket = (now - state.LastPacketTime).TotalSeconds;
            if (timeSinceLastPacket < 0.5) continue; // 최근 패킷이 있으면 스킵

            // 정지 시작 시간 설정
            state.StopStartTime ??= state.LastPacketTime;

            double stopDuration = (now - state.StopStartTime.Value).TotalSeconds;
            if (stopDuration >= CorridorBellConfig.StopThresholdSeconds)
            {
                double timeSinceLastPenalty = (now - state.LastPenaltyTime).TotalSeconds;
                if (timeSinceLastPenalty >= CorridorBellConfig.PenaltyCooldownSeconds)
                {
                    state.LastPenaltyTime = now;
                    state.StopStartTime = now;

                    results.Add((playerId,
                        new CorridorViolationResult
                        {
                            IsViolation = true,
                            CorruptionDelta = CorridorBellConfig.StopPenaltyCorruption,
                            Message = "복도에서 정지"
                        }));
                }
            }
        }

        return results;
    }
}

/// <summary>
///     복도 규칙 매니저 - MatchingId별로 복도 상태 관리
///     현재는 CSV 기반 규칙으로 전환되어 위반 체크 비활성화됨
/// </summary>
public class CorridorRuleManager
{
    private readonly ConcurrentDictionary<long, MatchingCorridorRuleState> _matchingStates = new();
    private Action<string>? _logAction;

    public void Initialize(Action<string>? logAction = null, Action<long, long, int>? onStopViolationCallback = null)
    {
        _logAction = logAction;
        _matchingStates.Clear();
        _logAction?.Invoke("CorridorRuleManager: Initialized (CSV-based rules - violation check disabled)");
    }

    /// <summary>
    ///     매칭 인스턴스의 복도 상태를 가져오거나 새로 생성
    /// </summary>
    private MatchingCorridorRuleState GetOrCreateMatchingState(long matchingId)
    {
        return _matchingStates.GetOrAdd(matchingId, id =>
        {
            var state = new MatchingCorridorRuleState();
            _logAction?.Invoke($"CorridorRuleManager: Created state for MatchingId={id}");
            return state;
        });
    }

    /// <summary>
    ///     플레이어 이동 시 복도 상태 업데이트
    /// </summary>
    public CorridorViolationResult CheckPlayerMove(long matchingId, long playerId, Vector3f position, Vector3f velocity,
        AreaType currentArea, int corridorRuleId)
    {
        var state = GetOrCreateMatchingState(matchingId);
        return state.CheckMove(playerId, position, velocity, currentArea, corridorRuleId);
    }

    /// <summary>
    ///     현재 매칭의 활성 복도 규칙 가져오기 (항상 None 반환)
    /// </summary>
    public CorridorRuleType GetActiveRule(long matchingId)
    {
        return CorridorRuleType.None;
    }

    /// <summary>
    ///     현재 매칭의 활성 복도 규칙 ID 가져오기 (항상 0 반환)
    /// </summary>
    public int GetActiveRuleId(long matchingId)
    {
        return 0;
    }

    /// <summary>
    ///     타이머 기반 정지 체크 (모든 매칭의 규칙 6 적용 플레이어)
    /// </summary>
    public List<(long MatchingId, long PlayerId, CorridorViolationResult Result)> CheckAllStoppedPlayersForRule6()
    {
        var results = new List<(long, long, CorridorViolationResult)>();

        foreach (var kvp in _matchingStates)
        {
            long matchingId = kvp.Key;
            var matchingState = kvp.Value;
            var violations = matchingState.CheckStoppedPlayersForRule6();

            foreach ((long playerId, var result) in violations) results.Add((matchingId, playerId, result));
        }

        return results;
    }

    /// <summary>
    ///     매칭 종료 시 해당 매칭의 상태 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out _))
            _logAction?.Invoke($"CorridorRuleManager: Removed state for MatchingId={matchingId}");
    }

    /// <summary>
    ///     플레이어 세션 종료 시 해당 플레이어 상태 정리
    /// </summary>
    public void RemovePlayerState(long matchingId, long playerId)
    {
        if (_matchingStates.TryGetValue(matchingId, out var state)) state.RemovePlayerState(playerId);
    }

    /// <summary>
    ///     전체 상태 초기화
    /// </summary>
    public void Reset()
    {
        _matchingStates.Clear();
        _logAction?.Invoke("CorridorRuleManager: All matching states cleared");
    }
}
