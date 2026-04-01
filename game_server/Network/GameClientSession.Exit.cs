using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    #region 로비 복귀

    /// <summary>
    ///     로비 복귀 요청 처리 (게임 완료 후)
    /// </summary>
    private Task HandleReturnToLobby(C_TO_G_RETURN_TO_LOBBY msg)
    {
        if (!PlayerId.HasValue)
        {
            Logger.LogWarning("HandleReturnToLobby: PlayerId not set");
            using var errorPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(false, ErrorCode.FATAL);
            Send(errorPacket);
            return Task.CompletedTask;
        }

        try
        {
            // 게임 종료 후 로비 복귀 (탈출 성공/실패 모두 허용)
            Logger.LogInformation("Player {PlayerId} returning to lobby", PlayerId);

            // 성공 응답 전송
            using var resultPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(true, ErrorCode.SUCCESS);
            Send(resultPacket);

            // 세션 정리 (Leave 콜백 호출)
            _onLeaveCallback.Invoke(this);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleReturnToLobby error for player {PlayerId}", PlayerId);
            using var errorPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(false, ErrorCode.FATAL);
            Send(errorPacket);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region 탈출 절차

    /// <summary>
    ///     탈출 절차 정보 전송 (게임 접속 시 자동 전송)
    /// </summary>
    private void SendExitStepInfo()
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);

            using var packet = PacketMaker.G_TO_C_EXIT_STEP_INFO(
                state.GroupId,
                state.CurrentStepOrder,
                state.Steps.Count,
                state.IsCompleted,
                state.LastAdvancedBy
            );
            Send(packet);

            Logger.LogInformation(
                "Sent exit step info to Player {PlayerId}: GroupId={GroupId}, CurrentStep={StepOrder}/{TotalSteps}, Completed={IsCompleted}, LastAdvancedBy={LastAdvancedBy}",
                PlayerId, state.GroupId, state.CurrentStepOrder, state.Steps.Count, state.IsCompleted,
                state.LastAdvancedBy);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendExitStepInfo error for player {PlayerId}", PlayerId);
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "탈출 절차 정보 전송 오류");
        }
    }

    /// <summary>
    ///     탈출 절차 다음 단계 진행 요청 처리
    /// </summary>
    private Task HandleExitAdvance(C_TO_G_EXIT_ADVANCE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        try
        {
            var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);

            // 클라이언트가 생각하는 현재 단계와 서버 단계 검증
            if (msg.CurrentStepOrder != state.CurrentStepOrder)
            {
                Logger.LogWarning(
                    "Player {PlayerId} exit advance step mismatch: client={ClientStep}, server={ServerStep}",
                    PlayerId, msg.CurrentStepOrder, state.CurrentStepOrder);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    false,
                    ErrorCode.FATAL,
                    false,
                    state.CurrentStepOrder
                );
                Send(errorPacket);
                return Task.CompletedTask;
            }

            // 이미 완료된 경우
            if (state.IsCompleted)
            {
                Logger.LogWarning("Player {PlayerId} tried to advance already completed exit procedure", PlayerId);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    false,
                    ErrorCode.FATAL,
                    true,
                    state.CurrentStepOrder
                );
                Send(errorPacket);
                return Task.CompletedTask;
            }

            // 다음 단계로 진행 (수동 진행)
            (bool success, bool escaped) = _exitInstanceManager.AdvanceStep(CurrentMapSubId, PlayerId.Value);

            if (success)
            {
                int newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    Logger.LogInformation("Game timer cleaned up after escape success: MatchingId={MatchingId}",
                        CurrentMapSubId);
                }

                // 요청자에게 결과 응답
                using var resultPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    true,
                    ErrorCode.SUCCESS,
                    escaped,
                    newStepOrder
                );
                Send(resultPacket);

                // 같은 인스턴스의 다른 플레이어들에게 브로드캐스트
                BroadcastExitStepUpdate(PlayerId.Value, newStepOrder, escaped);

                // 현재 Area의 Interactable 목록 다시 전송 (MissionActionText 갱신)
                if (CurrentArea != AreaType.None) SendInteractableList(CurrentArea);

                Logger.LogInformation("Player {PlayerId} advanced exit step: NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, newStepOrder, escaped);
            }
            else
            {
                Logger.LogWarning("Player {PlayerId} failed to advance exit step", PlayerId);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    false,
                    ErrorCode.FATAL,
                    false,
                    state.CurrentStepOrder
                );
                Send(errorPacket);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleExitAdvance error for player {PlayerId}", PlayerId);
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "탈출 절차 진행 오류");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     탈출 절차 단계 변경을 같은 인스턴스의 다른 플레이어들에게 브로드캐스트
    /// </summary>
    private void BroadcastExitStepUpdate(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var otherSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.PlayerId.HasValue).ToList();

        using var packet = PacketMaker.G_TO_C_EXIT_STEP_UPDATE(advancedByPlayerId, newStepOrder, escaped);
        foreach (var session in otherSessions)
        {
            session.Send(packet);

            // 다른 플레이어에게도 현재 Area의 Interactable 목록 전송 (MissionActionText 갱신)
            if (session.CurrentArea != AreaType.None) session.SendInteractableList(session.CurrentArea);
        }

        Logger.LogDebug("Broadcasted EXIT_STEP_UPDATE to {Count} players in instance {InstanceId}", otherSessions.Count,
            CurrentMapSubId);
    }

    /// <summary>
    ///     액션 완료 시 탈출 절차 확인 (target_interactable_action 조건 체크)
    /// </summary>
    private void CheckActionCompletedForExit(int interactId, int actionId)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            (bool advanced, bool escaped) =
                _exitInstanceManager.OnActionCompleted(CurrentMapSubId, interactId, actionId, PlayerId.Value);

            if (advanced)
            {
                var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);
                int newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    Logger.LogInformation(
                        "Game timer cleaned up after escape success (action completed): MatchingId={MatchingId}",
                        CurrentMapSubId);
                }

                // 진행 결과 브로드캐스트
                BroadcastExitStepUpdateToAll(PlayerId.Value, newStepOrder, escaped);

                Logger.LogInformation(
                    "Player {PlayerId} action completion advanced exit step: InteractId={InteractId}, ActionId={ActionId}, NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, interactId, actionId, newStepOrder, escaped);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CheckActionCompletedForExit error for player {PlayerId}", PlayerId);
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "탈출 절차 액션 확인 오류");
        }
    }

    /// <summary>
    ///     아이템 습득 시 탈출 절차 확인 (target_item_id 조건 체크)
    /// </summary>
    private void CheckAndAdvanceExitStep(int acquiredItemId)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            (bool advanced, bool escaped) =
                _exitInstanceManager.OnItemAcquired(CurrentMapSubId, acquiredItemId, PlayerId.Value);

            if (advanced)
            {
                var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);
                int newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    Logger.LogInformation(
                        "Game timer cleaned up after escape success (item acquired): MatchingId={MatchingId}",
                        CurrentMapSubId);
                }

                // 진행 결과 브로드캐스트
                BroadcastExitStepUpdateToAll(PlayerId.Value, newStepOrder, escaped);

                Logger.LogInformation(
                    "Player {PlayerId} item acquisition advanced exit step: ItemId={ItemId}, NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, acquiredItemId, newStepOrder, escaped);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CheckAndAdvanceExitStep error for player {PlayerId}", PlayerId);
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "탈출 절차 진행 확인 오류");
        }
    }

    /// <summary>
    ///     탈출 절차 단계 변경을 본인 포함 모든 플레이어에게 브로드캐스트
    /// </summary>
    private void BroadcastExitStepUpdateToAll(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        using var packet = PacketMaker.G_TO_C_EXIT_STEP_UPDATE(advancedByPlayerId, newStepOrder, escaped);
        foreach (var session in allSessions.Where(s => s.PlayerId.HasValue)) session.Send(packet);

        Logger.LogDebug("Broadcasted EXIT_STEP_UPDATE to ALL {Count} players in instance {InstanceId}",
            allSessions.Count, CurrentMapSubId);
    }

    #endregion
}
