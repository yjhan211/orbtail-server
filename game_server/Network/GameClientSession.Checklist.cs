using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

/// <summary>
///     체크리스트 (차기 재사용 보존, CHECKLIST_SYSTEM_ENABLED 동결): 정보 전송·진행·활동 시작/완료 핸들러.
/// </summary>
public partial class GameClientSession
{
    private void SendChecklistInfo()
    {
        if (!PlayerId.HasValue) return;

        const int roundNumber = 1; // 라운드 시스템 퇴역(#246) — 상시 단일 라운드
        var contribution = _checklistManager.GetPlayerContributions(CurrentMapSubId, new[] { PlayerId.Value })
            .FirstOrDefault();
        var msg = new G_TO_C_CHECKLIST_INFO
        {
            MatchingId = CurrentMapSubId,
            RoundNumber = roundNumber,
            ActiveTaskIds = _checklistManager.GetActiveTasks(CurrentMapSubId, PlayerId.Value)
                .Select(task => task.TaskId)
                .ToList(),
            CompletedTaskIds = _checklistManager.GetCompletedTaskIds(CurrentMapSubId, PlayerId.Value),
            ActiveTaskProgresses = _checklistManager.GetActiveTaskProgresses(CurrentMapSubId, PlayerId.Value),
            GeneralJobScore = contribution?.GeneralJobScore ?? 0f,
            ManittoRoleScore = contribution?.ManittoRoleScore ?? 0f,
            BonusScore = contribution?.BonusScore ?? 0f,
            Contribution = contribution?.Contribution ?? 0
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_INFO, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    public bool AdvanceTargetProximityChecklistProgress(float deltaSeconds)
    {
        if (!PlayerId.HasValue) return false;

        var result = _checklistManager.AdvanceActiveTaskProgress(
            CurrentMapSubId,
            PlayerId.Value,
            "MANITTO_STAY_NEAR_TARGET_20",
            deltaSeconds);
        if (!result.Changed)
            return false;

        if (result.Completion?.CompletedTask != null)
            Logger.LogInformation(
                "Checklist proximity task completed: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}",
                CurrentMapSubId, PlayerId.Value, result.Completion.CompletedTask.TaskId);

        SendChecklistInfo();
        return true;
    }

    private bool TryGetActiveInteractObjectChecklistTask(InteractableInfoData info, out ChecklistTaskData? task)
    {
        task = null;
        if (!PlayerId.HasValue || info == null) return false;

        task = _checklistManager.GetActiveTasks(CurrentMapSubId, PlayerId.Value)
            .FirstOrDefault(activeTask =>
                activeTask.Category == ChecklistTaskCategory.GeneralJob &&
                activeTask.CompletionEvent.Equals("interact_object", StringComparison.OrdinalIgnoreCase) &&
                (activeTask.AreaType <= 0 || activeTask.AreaType == info.ZoneId) &&
                (activeTask.ObjectType <= 0 || activeTask.ObjectType == (int)info.ObjectType) &&
                (activeTask.InteractId <= 0 || activeTask.InteractId == info.Id));
        return task != null;
    }

    private bool TryCompleteInteractObjectChecklist(InteractableInfoData info)
    {
        return TryCompleteInteractObjectChecklist(info, out _, out _, out _) == ErrorCode.SUCCESS;
    }

    private ErrorCode TryCompleteInteractObjectChecklist(
        InteractableInfoData info,
        out ChecklistTaskData? completedTask,
        out float awardedScore,
        out int awardedContribution)
    {
        completedTask = null;
        awardedScore = 0f;
        awardedContribution = 0;
        if (!PlayerId.HasValue) return ErrorCode.INVALID_GAME_STATE;
        if (!TryGetActiveInteractObjectChecklistTask(info, out var task) || task == null)
            return ErrorCode.INTERACTABLE_NOT_AVAILABLE;

        var result = _checklistManager.TryCompleteTask(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            (AreaType)info.ZoneId,
            info.Id,
            _inGameInventoryManager);
        completedTask = result.CompletedTask;
        if (result.ErrorCode != ErrorCode.SUCCESS)
        {
            Logger.LogInformation(
                "Checklist interact_object completion skipped: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}, Error={ErrorCode}",
                CurrentMapSubId, PlayerId.Value, task.TaskId, info.Id, result.ErrorCode);
            return result.ErrorCode;
        }

        awardedScore = result.AwardedScore;
        awardedContribution = result.AwardedContribution;
        _gameEventLogManager.LogSchoolActivityComplete(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            ((AreaType)info.ZoneId).ToString(),
            info.Id,
            result.AwardedScore,
            result.AwardedContribution,
            task.TitleKr,
            isBot: false);

        if (result.ConsumedItemUpdate != null)
            SendInGameInventoryUpdate(result.ConsumedItemUpdate);

        SendChecklistInfo();
        Logger.LogInformation(
            "Checklist interact_object completed: MatchingId={MatchingId}, PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}",
            CurrentMapSubId, PlayerId.Value, task.TaskId, info.Id);
        return ErrorCode.SUCCESS;
    }

    private Task HandleChecklistActivityStart(C_TO_G_CHECKLIST_ACTIVITY_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated)
        {
            SendChecklistActivityAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (IsRoundActionLocked(out _))
        {
            SendChecklistActivityAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("Checklist activity START unknown InteractId: {InteractId}", msg.InteractId);
            SendChecklistActivityAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (!TryGetActiveInteractObjectChecklistTask(info, out var task) || task == null)
        {
            Logger.LogDebug(
                "Checklist activity START unavailable: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            SendChecklistActivityAck(msg.InteractId, ErrorCode.INTERACTABLE_NOT_AVAILABLE, 0);
            return Task.CompletedTask;
        }

        int staminaCost = Math.Max(0, task.StaminaCost); // 업무 체력 보존(미션 단서 보상)은 미션 스택과 함께 퇴역 (#238)
        if (staminaCost > 0)
            ModifyStats(-staminaCost);

        _pendingChecklistActivityFinish.Add(msg.InteractId);
        _gameEventLogManager.LogSchoolActivityStart(
            CurrentMapSubId,
            PlayerId.Value,
            task.TaskId,
            ((AreaType)info.ZoneId).ToString(),
            msg.InteractId,
            task.TitleKr,
            isBot: false);

        Logger.LogInformation(
            "Checklist activity START: PlayerId={PlayerId}, TaskId={TaskId}, InteractId={InteractId}, StaminaCost={StaminaCost}",
            PlayerId, task.TaskId, msg.InteractId, staminaCost);

        SendChecklistActivityAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        BroadcastPlayerState(global::network.common.PlayerState.EXPLORE_1);
        return Task.CompletedTask;
    }

    private Task HandleChecklistActivityFinish(C_TO_G_CHECKLIST_ACTIVITY_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        if (!_pendingChecklistActivityFinish.Remove(msg.InteractId))
        {
            Logger.LogWarning(
                "Checklist activity FINISH without START or duplicated: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        ErrorCode errorCode = ErrorCode.SUCCESS;
        float awardedScore = 0f;
        int awardedContribution = 0;
        if (info == null)
        {
            Logger.LogWarning("Checklist activity FINISH unknown InteractId: {InteractId}", msg.InteractId);
            errorCode = ErrorCode.FATAL;
        }
        else
        {
            errorCode = TryCompleteInteractObjectChecklist(info, out _, out awardedScore, out awardedContribution);
        }

        Logger.LogInformation(
            "Checklist activity FINISH: PlayerId={PlayerId}, InteractId={InteractId}, ErrorCode={ErrorCode}",
            PlayerId, msg.InteractId, errorCode);

        SendChecklistActivityResult(msg.InteractId, errorCode, awardedScore, awardedContribution);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        return Task.CompletedTask;
    }

    private void SendChecklistActivityAck(int interactId, ErrorCode errorCode, int cooldownRemain)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_CHECKLIST_ACTIVITY_ACK
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            CooldownRemainSeconds = cooldownRemain
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_ACTIVITY_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    private void SendChecklistActivityResult(
        int interactId,
        ErrorCode errorCode,
        float awardedScore,
        int awardedContribution)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_CHECKLIST_ACTIVITY_RESULT
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            AwardedScore = errorCode == ErrorCode.SUCCESS ? awardedScore : 0f,
            AwardedContribution = errorCode == ErrorCode.SUCCESS ? Math.Max(0, awardedContribution) : 0
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_CHECKLIST_ACTIVITY_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }
}
