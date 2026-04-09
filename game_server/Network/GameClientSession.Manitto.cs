using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     마니또 전용 핸들러: 색출, 흔적 배치, 타겟 위치 추적.
/// </summary>
public partial class GameClientSession
{
    /// <summary>
    ///     미션 정보 전송 (게임 접속 시)
    /// </summary>
    private void SendMissionInfo()
    {
        if (!PlayerId.HasValue) return;

        var currentStep = _missionManager.GetCurrentStep(CurrentMapSubId, PlayerId.Value);
        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_MISSION_INFO, PlayerId.Value);
        var msg = new G_TO_C_MISSION_INFO
        {
            JobTitle = MyJobTitle,
            CurrentStep = state.CurrentStepOrder,
            TotalSteps = state.TotalSteps,
            TargetArea = currentStep?.TargetArea ?? 0,
            TargetInteractId = currentStep?.TargetInteractId ?? 0,
            TargetActionId = currentStep?.TargetActionId ?? 0
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     색출 요청 처리 (1회 한정)
    /// </summary>
    private Task HandleDetectManitto(C_TO_G_DETECT_MANITTO msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        var (isCorrect, errorCode) = _manittoChainManager.TryDetect(CurrentMapSubId, PlayerId.Value, msg.TargetPlayerId);

        // 결과 전송
        using var packet = Packet.Create((int)Protocol.G_TO_C_DETECT_RESULT, PlayerId.Value);
        var result = new G_TO_C_DETECT_RESULT
        {
            ErrorCode = errorCode,
            IsCorrect = isCorrect,
            TargetPlayerId = msg.TargetPlayerId
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 적중 시 마니또 탈락 처리
        if (isCorrect && errorCode == ErrorCode.SUCCESS)
        {
            _ = ProcessElimination(msg.TargetPlayerId, EliminationReason.DETECTED);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     플레이어 탈락 처리 + 체인 단절 브로드캐스트
    /// </summary>
    private Task ProcessElimination(long eliminatedPlayerId, EliminationReason reason)
    {
        var affected = _manittoChainManager.EliminatePlayer(CurrentMapSubId, eliminatedPlayerId, reason);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        // 1. 전체에게 탈락 알림
        using var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
        var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
        {
            PlayerId = eliminatedPlayerId,
            Reason = reason
        };
        eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
        foreach (var session in allSessions) session.Send(eliminatedPacket);

        // 세션 ManittoStatus 동기화
        foreach (var (playerId, newStatus) in affected)
        {
            var s = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (s != null) s.ManittoStatus = newStatus;
        }

        // 2. 영향받는 플레이어에게 개별 상태 변경 알림
        foreach (var (playerId, newStatus) in affected)
        {
            if (newStatus == ManittoStatus.ELIMINATED) continue; // 탈락자 본인은 이미 알림됨

            var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == playerId);
            if (targetSession == null) continue;

            using var chainPacket = Packet.Create((int)Protocol.G_TO_C_CHAIN_BREAK, playerId);
            var chainMsg = new G_TO_C_CHAIN_BREAK
            {
                EliminatedPlayerId = eliminatedPlayerId,
                NewStatus = newStatus
            };
            chainPacket.SetBody(MessagePackSerializer.Serialize(chainMsg));
            targetSession.Send(chainPacket);
        }

        // 3. 게임 종료 판정
        var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(CurrentMapSubId);
        if (isGameOver)
        {
            Logger.LogInformation("게임 종료! 최후의 1인: {WinnerId}", winnerId);
            using var endPacket = PacketMaker.G_TO_C_GAME_END(CurrentMapSubId, true);
            foreach (var session in allSessions) session.Send(endPacket);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     흔적 배치 요청 처리 (마니또 전용)
    /// </summary>
    private Task HandlePlaceTrace(C_TO_G_PLACE_TRACE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        const int placeTraceCost = 10; // 스태미나 소모 (가데이터)

        // 스태미나 부족
        if (Stamina < placeTraceCost)
        {
            using var failPacket = Packet.Create((int)Protocol.G_TO_C_PLACE_TRACE_RESULT, PlayerId.Value);
            var failMsg = new G_TO_C_PLACE_TRACE_RESULT
            {
                ErrorCode = ErrorCode.INSUFFICIENT_STAMINA,
                StaminaCost = placeTraceCost
            };
            failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
            Send(failPacket);
            return Task.CompletedTask;
        }

        // 스태미나 소모
        ModifyStats(staminaDelta: -placeTraceCost);

        // 성공 응답
        using var packet = Packet.Create((int)Protocol.G_TO_C_PLACE_TRACE_RESULT, PlayerId.Value);
        var result = new G_TO_C_PLACE_TRACE_RESULT
        {
            ErrorCode = ErrorCode.SUCCESS,
            StaminaCost = placeTraceCost
        };
        packet.SetBody(MessagePackSerializer.Serialize(result));
        Send(packet);

        // 흔적 브로드캐스트
        BroadcastTraceCreated(CurrentArea, msg.InteractId, "누군가 무언가를 남겼다...", false);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     마니또 → 타겟 구역 위치 전송
    /// </summary>
    public void SendTargetLocation()
    {
        if (!PlayerId.HasValue || TargetPlayerId == 0) return;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (targetSession == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_TARGET_LOCATION, PlayerId.Value);
        var msg = new G_TO_C_TARGET_LOCATION
        {
            TargetPlayerId = TargetPlayerId,
            AreaType = targetSession.CurrentArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     탐색 완료 시 미션 진행 체크
    /// </summary>
    public void CheckMissionProgress(AreaType area, int interactId, int actionId)
    {
        if (!PlayerId.HasValue) return;

        var result = _missionManager.TryCompleteStep(CurrentMapSubId, PlayerId.Value, area, interactId, actionId);
        if (result == null) return;

        // 스태미나 보상
        int prevStamina = Stamina;
        Stamina = Math.Min(Stamina + result.StaminaReward, MaxStamina);
        int staminaDelta = Stamina - prevStamina;
        using var statsPacket = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Stamina, staminaDelta, Corruption, 0);
        Send(statsPacket);

        if (result.IsAllCompleted)
        {
            // 미션 전체 완료
            using var completePacket = Packet.Create((int)Protocol.G_TO_C_MISSION_ALL_COMPLETE, PlayerId.Value);
            var completeMsg = new G_TO_C_MISSION_ALL_COMPLETE { JobTitle = MyJobTitle };
            completePacket.SetBody(MessagePackSerializer.Serialize(completeMsg));
            Send(completePacket);
        }
        else
        {
            // 단계 완료 + 다음 정보
            using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = result.CompletedStep,
                StaminaReward = result.StaminaReward,
                NextTargetArea = result.NextStep?.TargetArea ?? 0,
                NextTargetInteractId = result.NextStep?.TargetInteractId ?? 0,
                NextTargetActionId = result.NextStep?.TargetActionId ?? 0
            };
            stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
            Send(stepPacket);
        }

        // 흔적 생성 (같은 구역 플레이어에게 알림)
        BroadcastTraceCreated(result.TraceArea, result.TraceInteractId, result.TraceDescription, true);
    }

    /// <summary>
    ///     흔적 생성 브로드캐스트
    /// </summary>
    private void BroadcastTraceCreated(AreaType area, int interactId, string description, bool isMissionTrace)
    {
        if (!PlayerId.HasValue) return;

        var trace = new TraceInfo
        {
            TraceId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AreaType = area,
            InteractId = interactId,
            Description = description,
            PlacedByPlayerId = PlayerId.Value,
            IsMissionTrace = isMissionTrace
        };

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var areaSessionsExceptMe = allSessions
            .Where(s => s.CurrentArea == area && s.PlayerId != PlayerId)
            .ToList();

        if (areaSessionsExceptMe.Count == 0) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_TRACE_CREATED);
        var msg = new G_TO_C_TRACE_CREATED { Trace = trace };
        packet.SetBody(MessagePackSerializer.Serialize(msg));

        foreach (var session in areaSessionsExceptMe)
            session.Send(packet);
    }

    /// <summary>
    ///     스태미나/정신력이 0 이하일 때 탈락 체크
    /// </summary>
    public void CheckResourceElimination()
    {
        if (!PlayerId.HasValue) return;

        if (Stamina <= 0)
            _ = ProcessElimination(PlayerId.Value, EliminationReason.STAMINA_ZERO);
        else if (Corruption >= MaxCorruption)
            _ = ProcessElimination(PlayerId.Value, EliminationReason.MENTAL_ZERO);
    }
}
