using System.Collections.Generic;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     RNG 채집 2단계 흐름 (#134):
///       1) C_TO_G_RNG_COLLECT_START — RippleMarker 클릭 즉시
///          → stamina 차감 + cooldown 등록(short, 4초) + EXPLORE_START broadcast + cooldown broadcast + ACK
///       2) C_TO_G_RNG_COLLECT_FINISH — progress 1.5~2초 후
///          → RNG 결과 산출 + cooldown 갱신(30초) + EXPLORE_END broadcast + cooldown broadcast + RESULT
///     PendingFinish dict로 START가 미처리된 FINISH 거부 + 부정행위 차단.
/// </summary>
public partial class GameClientSession
{
    private const int RngCollectCooldownSeconds = 30;

    /// <summary>1단계 짧은 cooldown — progress 도중 차단용. progress 폐기/finish 미수신 시 자동 해제.</summary>
    private const int RngCollectStartCooldownSeconds = 4;

    private const int RngCollectStaminaCost = 5;

    /// <summary>START 처리됐으나 FINISH 대기 중인 InteractId — 매칭 단위 추적.
    /// FINISH 도착 시 이 set에 있어야 결과 산출 진행.</summary>
    private readonly HashSet<int> _pendingFinish = new();

    private Task HandleRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        // 쿨타임 체크 (인스턴스 단위)
        if (RngCollectCooldownStore.IsInCooldown(CurrentMapSubId, msg.InteractId, out int remaining))
        {
            Logger.LogDebug("RNG START 쿨타임 거부: PlayerId={PlayerId}, InteractId={InteractId}, 남은={Sec}s",
                PlayerId, msg.InteractId, remaining);
            SendRngCollectAck(msg.InteractId, ErrorCode.ACTION_ALREADY_EXPLORED, remaining);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("RNG START InteractId 미존재: {InteractId}", msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        // stamina 차감 (즉시) — 정신력 1:2 변환은 ModifyStats가 처리
        ModifyStats(-RngCollectStaminaCost);

        // 1단계 짧은 cooldown 등록 — progress 도중 차단. FINISH 도착 시 30초로 갱신.
        RngCollectCooldownStore.SetCooldown(CurrentMapSubId, msg.InteractId, RngCollectStartCooldownSeconds);

        // 사보타주 상태 자동 복구
        var currentState = _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
        if (currentState == (int)InteractableStateType.SABOTAGE)
            _sabotageManager.OnActionCompleted(CurrentMapSubId, msg.InteractId, 0);

        _pendingFinish.Add(msg.InteractId);

        Logger.LogInformation("RNG 채집 START: PlayerId={PlayerId}, InteractId={InteractId}",
            PlayerId, msg.InteractId);

        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        BroadcastRngCollectCooldown(msg.InteractId, RngCollectStartCooldownSeconds);
        return Task.CompletedTask;
    }

    private Task HandleRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        if (!_pendingFinish.Remove(msg.InteractId))
        {
            Logger.LogWarning("RNG FINISH — START 미수신 또는 중복: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("RNG FINISH InteractId 미존재: {InteractId}", msg.InteractId);
            return Task.CompletedTask;
        }

        var outcome = RngCollectCore.Resolve(
            matchingId: CurrentMapSubId,
            playerId: PlayerId.Value,
            jobTitle: MyJobTitle,
            info: info,
            missionManager: _missionManager,
            inventoryManager: _inGameInventoryManager,
            itemPoolManager: _itemPoolManager,
            isBot: false);

        // 부품 회수 시 패킷 송신
        if (outcome is { ResultType: 3, CollectedPart: not null })
        {
            ApplyStaminaReward(outcome.StaminaReward);

            using var partPacket = Packet.Create((int)Protocol.G_TO_C_PART_COLLECTED, PlayerId.Value);
            var partMsg = new G_TO_C_PART_COLLECTED
            {
                PartId = outcome.CollectedPart.PartId,
                PartNameKr = outcome.CollectedPart.PartNameKr,
                PartTier = (int)outcome.CollectedPart.PartTier,
                StaminaReward = outcome.StaminaReward
            };
            partPacket.SetBody(MessagePackSerializer.Serialize(partMsg));
            Send(partPacket);

            using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = outcome.CollectedPart.PartId,
                StaminaReward = outcome.StaminaReward,
                NextTargetArea = 0,
                NextTargetInteractId = 0,
                NextTargetActionId = 0
            };
            stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
            Send(stepPacket);

            _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
                $"RNG 부품 회수: {outcome.CollectedPart.PartNameKr} (체력+{outcome.StaminaReward})", isBot: false);
        }

        if (outcome.AddedInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedInventoryItem);

        Logger.LogInformation(
            "RNG 채집 FINISH: PlayerId={PlayerId}, InteractId={InteractId}, ResultType={Type}, ItemId={ItemId}",
            PlayerId, msg.InteractId, outcome.ResultType, outcome.ItemId);

        SendRngCollectResult(msg.InteractId, outcome.ResultType, outcome.ItemId,
            outcome.StaminaReward, RngCollectCooldownSeconds);

        // FINISH 시점에 30초 cooldown 갱신 broadcast (RngCollectCore.Resolve 내부에서 SetCooldown 30 호출됨)
        BroadcastRngCollectCooldown(msg.InteractId, RngCollectCooldownSeconds);
        return Task.CompletedTask;
    }

    private void SendRngCollectAck(int interactId, ErrorCode errorCode, int cooldownRemain)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_ACK
        {
            InteractId = interactId,
            ErrorCode = errorCode,
            CooldownRemainSeconds = cooldownRemain
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }

    /// <summary>
    ///     같은 매칭 인스턴스의 모든 플레이어 클라에게 InteractId + cooldown broadcast.
    /// </summary>
    private void BroadcastRngCollectCooldown(int interactId, int cooldownSeconds)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var msg = new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
        {
            InteractId = interactId,
            CooldownSeconds = cooldownSeconds
        };
        var body = MessagePackSerializer.Serialize(msg);
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue) continue;
            using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST, session.PlayerId.Value);
            packet.SetBody(body);
            session.Send(packet);
        }
    }

    private void SendRngCollectResult(int interactId, int resultType, int itemId,
        int staminaReward, int cooldownSeconds)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_RESULT
        {
            InteractId = interactId,
            ResultType = resultType,
            ItemId = itemId,
            StaminaReward = staminaReward,
            CooldownSeconds = cooldownSeconds
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }
}
