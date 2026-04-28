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
    ///     플레이어 미션 초기화 (게임 시작 시).
    ///     미션 데이터가 없는 직책은 경고 로그 후 완료 상태로 처리 (방어 로직, #24).
    /// </summary>
    public void InitializePlayer(long matchingId, long playerId, JobTitle jobTitle)
    {
        var matchingDict = _matchingStates.GetOrAdd(matchingId, _ => new ConcurrentDictionary<long, PlayerMissionState>());

        int totalSteps = GameMissionData.GetTotalSteps((short)jobTitle);

        // 방어: 미션 데이터 없는 직책 → 완료 상태로 초기화 (미션 없이 생존만)
        if (totalSteps == 0)
        {
            _logger.LogWarning("미션 데이터 없는 직책: PlayerId={PlayerId}, JobTitle={JobTitle} — 미션 없음으로 초기화",
                playerId, jobTitle);

            var emptyState = new PlayerMissionState
            {
                PlayerId = playerId,
                JobTitle = jobTitle,
                CurrentStepOrder = 1,
                TotalSteps = 0,
                IsCompleted = true
            };
            matchingDict[playerId] = emptyState;
            return;
        }

        var state = new PlayerMissionState
        {
            PlayerId = playerId,
            JobTitle = jobTitle,
            CurrentStepOrder = 1,
            TotalSteps = totalSteps,
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
    ///     어드민 운영툴용 직책별 전체 미션 단계 조회.
    ///     정적 GameMissionData 기준으로 조합하고, playerState 기준으로 현재/완료 여부를 마킹한다.
    /// </summary>
    public List<(int order, int targetArea, int targetInteractId, bool isCompleted, bool isCurrent)>
        GetAllStepsForAdmin(long matchingId, long playerId)
    {
        var result = new List<(int, int, int, bool, bool)>();
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return result;
        if (!matching.TryGetValue(playerId, out var state)) return result;

        var allSteps = GameMissionData.GetSteps((short)state.JobTitle);
        foreach (var step in allSteps)
        {
            bool isCompleted = state.IsCompleted || step.StepOrder < state.CurrentStepOrder;
            bool isCurrent = !state.IsCompleted && step.StepOrder == state.CurrentStepOrder;
            result.Add((step.StepOrder, step.TargetArea, step.TargetInteractId, isCompleted, isCurrent));
        }
        return result;
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
    ///     사보타주: 특정 interactId에 해당하는 미션 단계를 가진 플레이어의 목적지를 무작위 재설정.
    ///     반환: 영향받은 플레이어 목록 (playerId → 새 미션 정보)
    /// </summary>
    public List<(long playerId, int step, int newArea, int newInteractId, int newActionId)>
        RedirectMissionsByInteractId(long matchingId, int interactId, AreaClosureManager closureManager)
    {
        var affected = new List<(long, int, int, int, int)>();
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return affected;

        // 폐쇄되지 않은 구역 목록
        var openAreas = Enum.GetValues<AreaType>()
            .Where(a => a != AreaType.None && !closureManager.IsAreaClosed(matchingId, a))
            .ToList();
        if (openAreas.Count == 0) return affected;

        foreach (var (playerId, state) in matching)
        {
            if (state.IsCompleted) continue;

            var currentStep = GameMissionData.GetStep((short)state.JobTitle, state.CurrentStepOrder);
            if (currentStep == null || currentStep.TargetInteractId != interactId) continue;

            // 무작위 구역 + 해당 구역의 기존 오브젝트 ID 재활용 (액션은 유지)
            var newArea = openAreas[Random.Shared.Next(openAreas.Count)];

            affected.Add((playerId, state.CurrentStepOrder, (int)newArea,
                currentStep.TargetInteractId, currentStep.TargetActionId));

            _logger.LogInformation("미션 재설정: PlayerId={PlayerId}, Step={Step}, {OldArea}→{NewArea}",
                playerId, state.CurrentStepOrder, (AreaType)currentStep.TargetArea, newArea);
        }

        return affected;
    }

    /// <summary>
    ///     사보타주 4B (패키지 Y, #24): 대상 플레이어의 현재 미션 단계를 무효화 (강제 스킵, 보상 미지급).
    ///     반환: (성공 여부, 무효화된 단계, 다음 단계 정보)
    ///     GDD 2.5.4: "다음 단계 미션 무효화 — 해당 단계 완료 처리, 보상 미지급"
    /// </summary>
    public (bool success, int invalidatedStep, MissionStepData? nextStep) InvalidateCurrentStep(
        long matchingId, long targetPlayerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching))
            return (false, 0, null);
        if (!matching.TryGetValue(targetPlayerId, out var state))
            return (false, 0, null);
        if (state.IsCompleted)
            return (false, 0, null);

        int invalidated = state.CurrentStepOrder;
        state.CurrentStepOrder++;

        MissionStepData? nextStep = null;
        if (state.CurrentStepOrder > state.TotalSteps)
        {
            // 마지막 단계가 무효화되면 미션 전체 완료(보상 없이)
            state.IsCompleted = true;
            _logger.LogInformation("사보타주 4B — 마지막 단계 무효화(보상 없음): PlayerId={Target}, Step={Step}",
                targetPlayerId, invalidated);
        }
        else
        {
            nextStep = GameMissionData.GetStep((short)state.JobTitle, state.CurrentStepOrder);
            _logger.LogInformation("사보타주 4B — 미션 단계 무효화: PlayerId={Target}, Step={Step}→{Next}",
                targetPlayerId, invalidated, state.CurrentStepOrder);
        }

        return (true, invalidated, nextStep);
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
