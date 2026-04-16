using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

/// <summary>
///     사보타주 이벤트 정의
/// </summary>
public class SabotageEvent
{
    public AreaType TriggerArea { get; init; } // 이 Area에 진입하면 트리거
    public int InteractId { get; init; } // 상태 변경할 Interactable ID
    public int DelaySeconds { get; init; } // 트리거 후 지연 시간 (초)
    public int NewState { get; init; } // 변경할 상태 값

    // 타임아웃 설정
    public int TimeoutSeconds { get; init; } // 상태 변경 후 타임아웃 시간 (초), 0이면 타임아웃 없음
    public int TimeoutState { get; init; } // 타임아웃 시 변경할 상태
    public int TimeoutCorruption { get; init; } // 타임아웃 시 모든 플레이어 정신오염도 증가량

    // 해결 조건 (액션 완료 시 타임아웃 취소)
    public int ResolveActionId { get; init; } // 이 ActionId 완료 시 타임아웃 타이머 취소, 0이면 없음
}

/// <summary>
///     매칭별 사보타주 상태 관리
/// </summary>
public class MatchingSabotageState(long matchingId)
{
    // 현재 활성화된 사보타주 이벤트 (타임아웃 체크용)
    private readonly ConcurrentDictionary<int, SabotageEvent> _activeEvents = new();

    // 활성화된 시작 타이머들 (Key: InteractId)
    private readonly ConcurrentDictionary<int, Timer> _activeTimers = new();

    // Key: TriggerArea, Value: 해당 Area에 진입한 적 있는지
    private readonly ConcurrentDictionary<AreaType, bool> _areaTriggered = new();

    // 활성화된 타임아웃 타이머들 (Key: InteractId)
    private readonly ConcurrentDictionary<int, Timer> _timeoutTimers = new();

    public long MatchingId { get; } = matchingId;

    /// <summary>
    ///     Area 진입 시 트리거 여부 확인 (최초 1회만)
    /// </summary>
    public bool TryTriggerArea(AreaType areaType)
    {
        return _areaTriggered.TryAdd(areaType, true);
    }

    /// <summary>
    ///     시작 타이머 등록
    /// </summary>
    public void RegisterTimer(int interactId, Timer timer)
    {
        _activeTimers[interactId] = timer;
    }

    /// <summary>
    ///     시작 타이머 정리
    /// </summary>
    public void RemoveTimer(int interactId)
    {
        if (_activeTimers.TryRemove(interactId, out var timer)) timer.Dispose();
    }

    /// <summary>
    ///     타임아웃 타이머 등록
    /// </summary>
    public void RegisterTimeoutTimer(int interactId, Timer timer, SabotageEvent evt)
    {
        _timeoutTimers[interactId] = timer;
        _activeEvents[interactId] = evt;
    }

    /// <summary>
    ///     타임아웃 타이머 정리
    /// </summary>
    public void RemoveTimeoutTimer(int interactId)
    {
        if (_timeoutTimers.TryRemove(interactId, out var timer)) timer.Dispose();
        _activeEvents.TryRemove(interactId, out _);
    }

    /// <summary>
    ///     특정 InteractId에 대한 활성화된 이벤트 가져오기
    /// </summary>
    public SabotageEvent? GetActiveEvent(int interactId)
    {
        return _activeEvents.GetValueOrDefault(interactId);
    }

    /// <summary>
    ///     모든 타이머 정리
    /// </summary>
    public void Cleanup()
    {
        foreach (var timer in _activeTimers.Values) timer.Dispose();
        _activeTimers.Clear();

        foreach (var timer in _timeoutTimers.Values) timer.Dispose();
        _timeoutTimers.Clear();

        _activeEvents.Clear();
        _areaTriggered.Clear();
    }
}

/// <summary>
///     사보타주 이벤트 관리자
///     Area 진입 시 타이머 기반 상태 변경 이벤트 처리
/// </summary>
public class SabotageManager
{
    // 매칭별 사보타주 상태
    private readonly ConcurrentDictionary<long, MatchingSabotageState> _matchingStates = new();

    // 사보타주 이벤트 정의 (Area -> 이벤트 목록)
    private readonly Dictionary<AreaType, List<SabotageEvent>> _sabotageEvents = new();
    private Action<string>? _logAction;

    // 상태 변경 콜백 (matchingId, interactId, newState, triggerArea)
    private Action<long, int, int, AreaType>? _onStateChangeCallback;

    // 타임아웃 콜백 (matchingId, triggerArea, corruptionDelta)
    private Action<long, AreaType, int>? _onTimeoutCallback;

    public void Initialize(Action<string>? logAction = null)
    {
        _logAction = logAction;

        // 사보타주 이벤트 정의 (하드코딩 - 추후 CSV로 이동 가능)
        _sabotageEvents.Clear();

        // 교무실(Area 11) 전화기 사보타주: 5초 후 전화벨이 울림, 30초 내에 받지 않으면 타임아웃
        RegisterSabotageEvent(new SabotageEvent
        {
            TriggerArea = AreaType.StaffRoom, // 교무실
            InteractId = 701000027, // 전화기
            DelaySeconds = 5,
            NewState = (int)InteractableStateType.SABOTAGE, // 전화벨 울림 상태

            // 타임아웃 설정
            TimeoutSeconds = 30, // 30초 후 타임아웃
            TimeoutState = (int)InteractableStateType.DEFAULT, // 타임아웃 시 기본 상태로 복귀
            TimeoutCorruption = 20, // 모든 플레이어 정신오염도 +20

            // 해결 조건: state=SABOTAGE인 액션 중 하나를 완료하면 타임아웃 취소
            ResolveActionId = 0 // 0 = InteractId의 아무 액션이나 완료하면 해결
        });

        // 강당(Area 12) 농구공 사보타주: 20초 후 농구공 활성화, 30초 내에 처리 안하면 타임아웃
        RegisterSabotageEvent(new SabotageEvent
        {
            TriggerArea = AreaType.Gym, // 강당
            InteractId = 701000056, // 농구공
            DelaySeconds = 20,
            NewState = (int)InteractableStateType.SABOTAGE,

            // 타임아웃 설정
            TimeoutSeconds = 30, // 30초 후 타임아웃
            TimeoutState = (int)InteractableStateType.DEFAULT,
            TimeoutCorruption = 20, // 모든 플레이어 정신오염도 +20

            ResolveActionId = 0
        });

        _logAction?.Invoke($"SabotageManager: Initialized with {_sabotageEvents.Count} trigger areas");
    }

    /// <summary>
    ///     사보타주 이벤트 등록
    /// </summary>
    private void RegisterSabotageEvent(SabotageEvent evt)
    {
        if (!_sabotageEvents.TryGetValue(evt.TriggerArea, out var events))
        {
            events = new List<SabotageEvent>();
            _sabotageEvents[evt.TriggerArea] = events;
        }

        events.Add(evt);
    }

    /// <summary>
    ///     상태 변경 콜백 설정
    /// </summary>
    public void SetStateChangeCallback(Action<long, int, int, AreaType> callback)
    {
        _onStateChangeCallback = callback;
    }

    /// <summary>
    ///     타임아웃 콜백 설정
    /// </summary>
    public void SetTimeoutCallback(Action<long, AreaType, int> callback)
    {
        _onTimeoutCallback = callback;
    }

    /// <summary>
    ///     Area 진입 시 호출 - 사보타주 타이머 시작
    /// </summary>
    public void OnPlayerEnterArea(long matchingId, AreaType areaType)
    {
        // 해당 Area에 등록된 사보타주 이벤트가 없으면 무시
        if (!_sabotageEvents.TryGetValue(areaType, out var events)) return;

        // 매칭 상태 가져오기
        var matchingState = _matchingStates.GetOrAdd(matchingId, id => new MatchingSabotageState(id));

        // 해당 Area 최초 진입인지 확인
        if (!matchingState.TryTriggerArea(areaType))
            // 이미 트리거됨
            return;

        _logAction?.Invoke(
            $"SabotageManager: Area {areaType} triggered for MatchingId={matchingId}, starting {events.Count} sabotage events");

        // 각 사보타주 이벤트에 대해 타이머 시작
        foreach (var evt in events) StartSabotageTimer(matchingId, evt);
    }

    /// <summary>
    ///     사보타주 타이머 시작
    /// </summary>
    private void StartSabotageTimer(long matchingId, SabotageEvent evt)
    {
        var matchingState = _matchingStates.GetOrAdd(matchingId, id => new MatchingSabotageState(id));

        _logAction?.Invoke(
            $"SabotageManager: Starting timer for InteractId={evt.InteractId}, delay={evt.DelaySeconds}s, newState={evt.NewState}");

        var timer = new Timer(_ => { OnSabotageTimerElapsed(matchingId, evt); }, null,
            TimeSpan.FromSeconds(evt.DelaySeconds), Timeout.InfiniteTimeSpan);

        matchingState.RegisterTimer(evt.InteractId, timer);
    }

    /// <summary>
    ///     사보타주 타이머 완료 시 호출
    /// </summary>
    private void OnSabotageTimerElapsed(long matchingId, SabotageEvent evt)
    {
        _logAction?.Invoke(
            $"SabotageManager: Timer elapsed for MatchingId={matchingId}, InteractId={evt.InteractId}, setting state to {evt.NewState}");

        // 시작 타이머 정리
        if (_matchingStates.TryGetValue(matchingId, out var matchingState)) matchingState.RemoveTimer(evt.InteractId);

        // 콜백 호출 (상태 변경 + 브로드캐스트)
        _onStateChangeCallback?.Invoke(matchingId, evt.InteractId, evt.NewState, evt.TriggerArea);

        // 타임아웃 타이머 시작 (설정되어 있는 경우)
        if (evt.TimeoutSeconds > 0) StartTimeoutTimer(matchingId, evt);
    }

    /// <summary>
    ///     타임아웃 타이머 시작
    /// </summary>
    private void StartTimeoutTimer(long matchingId, SabotageEvent evt)
    {
        var matchingState = _matchingStates.GetOrAdd(matchingId, id => new MatchingSabotageState(id));

        _logAction?.Invoke(
            $"SabotageManager: Starting timeout timer for InteractId={evt.InteractId}, timeout={evt.TimeoutSeconds}s");

        var timer = new Timer(_ => { OnTimeoutTimerElapsed(matchingId, evt); }, null,
            TimeSpan.FromSeconds(evt.TimeoutSeconds), Timeout.InfiniteTimeSpan);

        matchingState.RegisterTimeoutTimer(evt.InteractId, timer, evt);
    }

    /// <summary>
    ///     타임아웃 타이머 완료 시 호출
    /// </summary>
    private void OnTimeoutTimerElapsed(long matchingId, SabotageEvent evt)
    {
        _logAction?.Invoke(
            $"SabotageManager: TIMEOUT for MatchingId={matchingId}, InteractId={evt.InteractId}, applying corruption +{evt.TimeoutCorruption}");

        // 타임아웃 타이머 정리
        if (_matchingStates.TryGetValue(matchingId, out var matchingState))
            matchingState.RemoveTimeoutTimer(evt.InteractId);

        // 상태를 타임아웃 상태로 변경
        _onStateChangeCallback?.Invoke(matchingId, evt.InteractId, evt.TimeoutState, evt.TriggerArea);

        // 타임아웃 콜백 호출 (모든 플레이어에게 정신오염도 증가)
        _onTimeoutCallback?.Invoke(matchingId, evt.TriggerArea, evt.TimeoutCorruption);
    }

    /// <summary>
    ///     액션 완료 시 호출 - 타임아웃 타이머 취소
    /// </summary>
    public void OnActionCompleted(long matchingId, int interactId, int actionId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matchingState)) return;

        var activeEvent = matchingState.GetActiveEvent(interactId);
        if (activeEvent == null) return;

        // ResolveActionId가 0이면 해당 InteractId의 아무 액션이나 완료해도 해결
        // ResolveActionId가 특정 값이면 해당 ActionId만 해결
        if (activeEvent.ResolveActionId == 0 || activeEvent.ResolveActionId == actionId)
        {
            _logAction?.Invoke(
                $"SabotageManager: Sabotage resolved for MatchingId={matchingId}, InteractId={interactId}, ActionId={actionId}");

            // 타임아웃 타이머 취소
            matchingState.RemoveTimeoutTimer(interactId);

            // 상태를 기본으로 복귀 (타임아웃 상태가 아닌 해결 상태)
            _onStateChangeCallback?.Invoke(matchingId, interactId, 0, activeEvent.TriggerArea);
        }
    }

    /// <summary>
    ///     매칭 종료 시 정리
    /// </summary>
    public void RemoveMatchingState(long matchingId)
    {
        if (_matchingStates.TryRemove(matchingId, out var state))
        {
            state.Cleanup();
            _logAction?.Invoke($"SabotageManager: Cleaned up state for MatchingId={matchingId}");
        }
    }

    /// <summary>
    ///     전체 초기화
    /// </summary>
    public void Reset()
    {
        foreach (var state in _matchingStates.Values) state.Cleanup();
        _matchingStates.Clear();
        _logAction?.Invoke("SabotageManager: All states cleared");
    }
}
