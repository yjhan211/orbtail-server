using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private Task HandleExploreStart(C_TO_G_EXPLORE_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        // 권고안 B 2026-05-05: Stamina 0이어도 탐색 가능 — ModifyStats가 Cor 1:2 변환.

        // 이미 탐색 중이면 무시
        if (CurrentState == PlayerState.Exploring)
        {
            Logger.LogWarning("Player {PlayerId} already exploring, ignoring explore start", PlayerId);
            SendExploreResult(false, 0, 0, 0, ErrorCode.EXPLORE_ALREADY_IN_PROGRESS);
            return Task.CompletedTask;
        }

        Logger.LogInformation("Player {PlayerId} started exploring InteractId={InteractId}", PlayerId, msg.InteractId);

        // 상태 변경
        CurrentState = PlayerState.Exploring;
        CurrentExploringInteractId = msg.InteractId;

        // 같은 Area의 다른 플레이어들에게 탐색 시작 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea);

        using var packet = PacketMaker.G_TO_C_EXPLORE_START(PlayerId.Value, msg.InteractId);
        foreach (var session in sameAreaSessions) session.Send(packet);

        Logger.LogDebug("Broadcasted EXPLORE_START to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);

        return Task.CompletedTask;
    }

    private Task HandleExploreSelect(C_TO_G_EXPLORE_SELECT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 탐색 중이 아닌 상태에서 선택 요청
        if (CurrentState != PlayerState.Exploring)
        {
            Logger.LogWarning("Player {PlayerId} not exploring, cannot select: State={State}",
                PlayerId, CurrentState);
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.EXPLORE_NOT_IN_PROGRESS);
            return Task.CompletedTask;
        }

        // 다른 오브젝트를 탐색 중
        if (CurrentExploringInteractId != msg.InteractId)
        {
            Logger.LogWarning(
                "Player {PlayerId} exploring different object: ExploringId={ExploringId}, RequestedId={RequestedId}",
                PlayerId, CurrentExploringInteractId, msg.InteractId);
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.INTERACTABLE_NOT_FOUND);
            return Task.CompletedTask;
        }

        Logger.LogInformation("Player {PlayerId} selected action: InteractId={InteractId}, ActionId={ActionId}",
            PlayerId, msg.InteractId, msg.ActionId);

        // RequireItemId 체크 - 필요한 아이템이 있는지 확인
        var interactableForCheck = GameInteractableData.Get(msg.InteractId);
        var actionDataForCheck = interactableForCheck.Actions.FirstOrDefault(a => a.ActionId == msg.ActionId);
        if (actionDataForCheck is { RequireItemId: > 0 })
        {
            var playerInventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            bool hasRequiredItem = playerInventory.GetItemCount(actionDataForCheck.RequireItemId) > 0;
            if (!hasRequiredItem)
            {
                Logger.LogWarning(
                    "Player {PlayerId} missing required item {ItemId} for InteractId={InteractId}, ActionId={ActionId}",
                    PlayerId, actionDataForCheck.RequireItemId, msg.InteractId, msg.ActionId);
                SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.REQUIRED_ITEM_MISSING);
                return Task.CompletedTask;
            }
        }

        // RequireAction 체크 - 선행 액션이 완료되었는지 확인 (형식: "interactableId_actionId")
        if (actionDataForCheck != null && !string.IsNullOrEmpty(actionDataForCheck.RequireAction))
        {
            string[] parts = actionDataForCheck.RequireAction.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out int reqInteractId) &&
                int.TryParse(parts[1], out int reqActionId))
            {
                var reqActionState = _interactableStateManager.GetState(CurrentMapSubId, reqInteractId, reqActionId);
                if (reqActionState == null || !reqActionState.IsExplored)
                {
                    Logger.LogWarning(
                        "Player {PlayerId} required action not completed: {RequireAction} for InteractId={InteractId}, ActionId={ActionId}",
                        PlayerId, actionDataForCheck.RequireAction, msg.InteractId, msg.ActionId);
                    SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.REQUIRED_ACTION_NOT_COMPLETED);
                    return Task.CompletedTask;
                }
            }
        }

        // 권고안 B 2026-05-05: Stamina 부족해도 ModifyStats가 Cor 1:2 변환 — 사전 차단 제거.

        // State 체크 - 액션의 state가 현재 Interactable state와 일치하는지 확인
        // state=0은 기본 상태(항상 가능), state>0은 해당 상태일 때만 가능
        if (actionDataForCheck is { State: > 0 })
        {
            int currentInteractableState =
                _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
            if (currentInteractableState != actionDataForCheck.State)
            {
                Logger.LogWarning(
                    "Player {PlayerId} action state mismatch: ActionState={ActionState}, CurrentState={CurrentState} for InteractId={InteractId}, ActionId={ActionId}",
                    PlayerId, actionDataForCheck.State, currentInteractableState, msg.InteractId, msg.ActionId);
                SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.ACTION_NOT_FOUND);
                return Task.CompletedTask;
            }
        }

        // 탐색 처리 (InteractableStateManager에서 상태 업데이트 - MatchingId별 독립 관리)
        bool success = _interactableStateManager.TryExplore(CurrentMapSubId, msg.InteractId, msg.ActionId,
            PlayerId.Value, out _);

        if (success)
        {
            Logger.LogInformation(
                "Player {PlayerId} explored InteractId={InteractId}, ActionId={ActionId} successfully",
                PlayerId, msg.InteractId, msg.ActionId);

            // 스태미나 차감 (액션별 stamina_cost)
            var interactable = GameInteractableData.Get(msg.InteractId);
            int currentInteractableState =
                _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
            // 현재 state에 맞는 액션 데이터 가져오기 (state=0은 기본, state>0은 특수 상태)
            var actionData =
                interactable.Actions.FirstOrDefault(a =>
                    a.ActionId == msg.ActionId && a.State == currentInteractableState)
                ?? interactable.Actions.FirstOrDefault(a => a.ActionId == msg.ActionId && a.State == 0);
            if (actionData is { StaminaCost: > 0 })
            {
                ModifyStats(-actionData.StaminaCost);
                Logger.LogInformation(
                    "Player {PlayerId} stamina reduced by {Cost} for InteractId={InteractId}, ActionId={ActionId}",
                    PlayerId, actionData.StaminaCost, msg.InteractId, msg.ActionId);
            }

            // RequireItemId 아이템 소모
            if (actionData is { RequireItemId: > 0 })
            {
                var removedItem =
                    _inGameInventoryManager.RemoveItemByItemId(CurrentMapSubId, PlayerId.Value,
                        actionData.RequireItemId);
                if (removedItem != null)
                {
                    // Count=0으로 설정해서 클라이언트에 삭제 알림
                    removedItem.Count = 0;
                    SendInGameInventoryUpdate(removedItem);
                    Logger.LogInformation(
                        "Player {PlayerId} consumed required item {ItemId} for InteractId={InteractId}, ActionId={ActionId}",
                        PlayerId, actionData.RequireItemId, msg.InteractId, msg.ActionId);
                }
            }

            // 상호작용 규칙 위반 체크 (금지된 액션 수행)
            bool isViolation = _interactRuleManager.IsForbiddenAction(CurrentMapSubId, msg.InteractId, msg.ActionId);

            // 사보타주 규칙 체크 (state > 0인 액션의 경우, 위반 액션과 비교)
            if (actionData is { State: > 0 })
            {
                var sabotageRule = _areaRuleManager.GetRuleForInteract(CurrentMapSubId, msg.InteractId);
                if (sabotageRule is { TargetActionId: > 0 })
                {
                    // 규칙의 target_action_id와 같은 액션을 선택하면 위반
                    isViolation = msg.ActionId == sabotageRule.TargetActionId;
                    Logger.LogInformation(
                        "Player {PlayerId} sabotage action: InteractId={InteractId}, ActionId={ActionId}, ForbiddenActionId={ForbiddenActionId}, IsViolation={IsViolation}",
                        PlayerId, msg.InteractId, msg.ActionId, sabotageRule.TargetActionId, isViolation);
                }
            }

            if (isViolation)
                Logger.LogInformation("Player {PlayerId} violated rule by InteractId={InteractId}, ActionId={ActionId}",
                    PlayerId, msg.InteractId, msg.ActionId);

            // 액션 결과 처리 (규칙 위반 시 result_* 값, 안전한 선택 시 safe_result_* 값)
            (var resultType, int resultId, int resultAmount) =
                _interactableStateManager.GetActionResult(msg.InteractId, msg.ActionId, isViolation);
            int rewardItemId = 0;

            switch (resultType)
            {
                case ActionResultType.REWARD_POOL:
                    // 풀에서 다음 아이템 가져오기 (resultId = pool_id, 상호작용 대상별 독립 풀)
                    int? itemFromPool = _itemPoolManager.GetNextItemFromPool(CurrentMapSubId, msg.InteractId, resultId);
                    if (itemFromPool.HasValue)
                    {
                        rewardItemId = itemFromPool.Value;
                        AddInGameItem(rewardItemId);
                        Logger.LogInformation("Player {PlayerId} received reward from pool {PoolId}: ItemId={ItemId}",
                            PlayerId, resultId, rewardItemId);
                    }

                    break;

                case ActionResultType.DEBUFF_CORRUPTION:
                    ModifyStats(corruptionDelta: resultAmount);
                    Logger.LogInformation("Player {PlayerId} received corruption debuff: +{Amount}", PlayerId,
                        resultAmount);
                    break;

                case ActionResultType.DEBUFF_STAMINA:
                    ModifyStats(-resultAmount);
                    Logger.LogInformation("Player {PlayerId} received stamina debuff: -{Amount}", PlayerId,
                        resultAmount);
                    break;

                case ActionResultType.BUFF_CORRUPTION:
                    ModifyStats(corruptionDelta: -resultAmount);
                    Logger.LogInformation("Player {PlayerId} received corruption buff: -{Amount}", PlayerId,
                        resultAmount);
                    break;

                case ActionResultType.BUFF_STAMINA:
                    ModifyStats(resultAmount);
                    Logger.LogInformation("Player {PlayerId} received stamina buff: +{Amount}", PlayerId, resultAmount);
                    break;
            }

            // 미션 진행 체크 (해당 구역/오브젝트/액션이 현재 미션과 일치하면 완료)
            CheckMissionProgress(CurrentArea, msg.InteractId, msg.ActionId);

            // 흔적 발견 체크 (오브젝트에 미발견 흔적이 있으면 발견 처리)
            CheckTraceDiscovery(msg.InteractId);

            // 사보타주 해결 체크 (전화 받기 등)
            _sabotageManager.OnActionCompleted(CurrentMapSubId, msg.InteractId, msg.ActionId);

            // 성공 응답 (최종 결정된 아이템 ID 전송, 규칙 위반 여부 포함)
            SendExploreResult(true, msg.InteractId, msg.ActionId, rewardItemId, ErrorCode.SUCCESS, isViolation);

            // 같은 Area의 모든 플레이어에게 상태 업데이트 브로드캐스트
            // SINGLE 타입이면 모든 액션을 탐색 완료로 브로드캐스트 (마커 제거용)
            if (interactable.InteractionType == InteractionType.SINGLE)
                foreach (var action in interactable.Actions)
                    BroadcastInteractableUpdate(msg.InteractId, action.ActionId, true, PlayerId.Value);
            else
                BroadcastInteractableUpdate(msg.InteractId, msg.ActionId, true, PlayerId.Value);
        }
        else
        {
            Logger.LogInformation(
                "Player {PlayerId} tried to explore already explored action: InteractId={InteractId}, ActionId={ActionId}",
                PlayerId, msg.InteractId, msg.ActionId);

            // 이미 탐색됨 - 실패 응답
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.ACTION_ALREADY_EXPLORED);
        }

        // SELECT 후에도 Exploring 상태 유지 (END 패킷으로 종료)
        return Task.CompletedTask;
    }

    private Task HandleExploreEnd(C_TO_G_EXPLORE_END msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 탐색 중이 아니면 무시
        if (CurrentState != PlayerState.Exploring)
        {
            Logger.LogWarning("Player {PlayerId} not exploring, ignoring explore end", PlayerId);
            SendExploreResult(false, 0, 0, 0, ErrorCode.EXPLORE_NOT_IN_PROGRESS);
            return Task.CompletedTask;
        }

        Logger.LogInformation("Player {PlayerId} ended exploring InteractId={InteractId}", PlayerId, msg.InteractId);

        // 탐색 종료 처리
        EndExplore();

        return Task.CompletedTask;
    }

    private void SendExploreResult(bool success, int interactId, int actionId, int itemId, ErrorCode errorCode,
        bool isViolation = false)
    {
        if (!PlayerId.HasValue) return;

        using var packet =
            PacketMaker.G_TO_C_EXPLORE_RESULT(success, interactId, actionId, itemId, errorCode, isViolation);
        Send(packet);
    }

    private void BroadcastInteractableUpdate(int interactId, int actionId, bool isExplored, long exploredBy)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);

        using var packet = PacketMaker.G_TO_C_INTERACTABLE_UPDATE(interactId, actionId, isExplored, exploredBy);
        foreach (var session in sameAreaSessions) session.Send(packet);

        Logger.LogDebug("Broadcasted INTERACTABLE_UPDATE to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);
    }

    private void EndExplore()
    {
        if (!PlayerId.HasValue) return;

        // 상태 복원
        CurrentState = PlayerState.Idle;
        CurrentExploringInteractId = null;

        // 같은 Area의 다른 플레이어들에게 탐색 종료 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea);

        using var packet = PacketMaker.G_TO_C_EXPLORE_END(PlayerId.Value);
        foreach (var session in sameAreaSessions) session.Send(packet);

        Logger.LogDebug("Broadcasted EXPLORE_END to {Count} players in Area {Area}", sameAreaSessions.Count,
            CurrentArea);
    }
}
