using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     인스턴스별 플레이어 미션 진행 관리.
///     직책(JobTitle)별 단계별 미션 추적, 완료 판정, 흔적 생성.
/// </summary>
public class MissionManager
{
    // matchingId → (playerId → MissionState)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, PlayerMissionState>> _matchingStates = new();
    private readonly ILogger _logger;

    public MissionManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     플레이어 미션 초기화 (게임 시작 시)
    /// </summary>
    public void InitializePlayer(long matchingId, long playerId, JobTitle jobTitle)
    {
        var matchingDict = _matchingStates.GetOrAdd(matchingId, _ => new ConcurrentDictionary<long, PlayerMissionState>());

        var state = new PlayerMissionState
        {
            PlayerId = playerId,
            JobTitle = jobTitle,
            CurrentStepOrder = 1,
            TotalSteps = GameMissionData.GetTotalSteps((short)jobTitle),
            IsCompleted = false
        };

        matchingDict[playerId] = state;
        _logger.LogInformation("미션 초기화: PlayerId={PlayerId}, 직책={JobTitle}, 총 {Total}단계",
            playerId, jobTitle, state.TotalSteps);
    }

    /// <summary>
    ///     현재 미션 단계 정보 조회
    /// </summary>
    public MissionStepData? GetCurrentStep(long matchingId, long playerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        if (!matching.TryGetValue(playerId, out var state)) return null;
        if (state.IsCompleted) return null;

        return GameMissionData.GetStep((short)state.JobTitle, state.CurrentStepOrder);
    }

    /// <summary>
    ///     미션 상태 조회
    /// </summary>
    public PlayerMissionState? GetState(long matchingId, long playerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        return matching.GetValueOrDefault(playerId);
    }

    /// <summary>
    ///     미션 단계 완료 시도. 해당 구역/오브젝트/액션이 현재 미션과 일치하면 완료 처리.
    /// </summary>
    public MissionCompleteResult? TryCompleteStep(long matchingId, long playerId, AreaType area, int interactId, int actionId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        if (!matching.TryGetValue(playerId, out var state)) return null;
        if (state.IsCompleted) return null;

        var currentStep = GameMissionData.GetStep((short)state.JobTitle, state.CurrentStepOrder);
        if (currentStep == null) return null;

        // 구역, 오브젝트, 액션 모두 일치해야 완료
        if (currentStep.TargetArea != (int)area) return null;
        if (currentStep.TargetInteractId != interactId) return null;
        if (currentStep.TargetActionId != actionId) return null;

        int completedStep = state.CurrentStepOrder;
        state.CurrentStepOrder++;

        MissionStepData? nextStep = null;
        if (state.CurrentStepOrder > state.TotalSteps)
        {
            state.IsCompleted = true;
            _logger.LogInformation("미션 전체 완료: PlayerId={PlayerId}, 직책={JobTitle}", playerId, state.JobTitle);
        }
        else
        {
            nextStep = GameMissionData.GetStep((short)state.JobTitle, state.CurrentStepOrder);
        }

        _logger.LogInformation("미션 단계 완료: PlayerId={PlayerId}, Step={Step}/{Total}, 보상={Reward}",
            playerId, completedStep, state.TotalSteps, currentStep.StaminaReward);

        return new MissionCompleteResult
        {
            CompletedStep = completedStep,
            StaminaReward = currentStep.StaminaReward,
            TraceDescription = currentStep.TraceDescription,
            TraceArea = area,
            TraceInteractId = interactId,
            IsAllCompleted = state.IsCompleted,
            NextStep = nextStep
        };
    }

    /// <summary>
    ///     현재 미션 목적지가 폐쇄되었는지 확인
    /// </summary>
    public bool IsCurrentMissionAreaClosed(long matchingId, long playerId, AreaClosureManager closureManager)
    {
        var step = GetCurrentStep(matchingId, playerId);
        if (step == null) return false;

        return closureManager.IsAreaClosed(matchingId, (AreaType)step.TargetArea);
    }

    /// <summary>
    ///     매칭 정리
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _matchingStates.TryRemove(matchingId, out _);
    }
}

public class PlayerMissionState
{
    public long PlayerId { get; set; }
    public JobTitle JobTitle { get; set; }
    public int CurrentStepOrder { get; set; }
    public int TotalSteps { get; set; }
    public bool IsCompleted { get; set; }
}

public class MissionCompleteResult
{
    public int CompletedStep { get; set; }
    public int StaminaReward { get; set; }
    public string TraceDescription { get; set; } = "";
    public AreaType TraceArea { get; set; }
    public int TraceInteractId { get; set; }
    public bool IsAllCompleted { get; set; }
    public MissionStepData? NextStep { get; set; }
}
