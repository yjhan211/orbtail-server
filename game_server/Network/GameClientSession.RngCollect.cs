using System.Collections.Generic;
using System.Linq;
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
    private const int RngCollectGiftResultType = 5;
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
        if (IsRoundActionLocked(out _))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("RNG START InteractId 미존재: {InteractId}", msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (HasAvailableMissionActionTarget(info))
        {
            Logger.LogDebug("RNG START mission action target blocked: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.INTERACTABLE_NOT_AVAILABLE, 0);
            return Task.CompletedTask;
        }

        int staminaCost = ApplyDutyStaminaSaverToCost(RngCollectStaminaCost);

        // stamina 차감 (즉시) — 정신력 1:2 변환은 ModifyStats가 처리
        ModifyStats(-staminaCost);

        // 사보타주 상태 자동 복구
        var currentState = _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
        if (currentState == (int)InteractableStateType.SABOTAGE)
            _sabotageManager.OnActionCompleted(CurrentMapSubId, msg.InteractId, 0);

        _pendingFinish.Add(msg.InteractId);

        Logger.LogInformation("RNG 채집 START: PlayerId={PlayerId}, InteractId={InteractId}",
            PlayerId, msg.InteractId);

        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        // EXPLORE_1 상태 broadcast — 같은 영역 모든 클라(본인 포함)가 받아 Player.Info.State 갱신.
        BroadcastPlayerState(global::network.common.PlayerState.EXPLORE_1);
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

        if (TryHandleGiftDiscoveryBeforeCollect(msg.InteractId, out var otherGiftDiscovery))
            return Task.CompletedTask;

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

            StoreTrace((AreaType)info.ZoneId, msg.InteractId,
                GetMissionCollectTraceDescription(outcome.CompletedMissionNodeIds), true);
        }

        foreach (var extraCollectResult in outcome.ExtraCollectedParts)
        {
            if (extraCollectResult.Part == null) continue;

            using var partPacket = Packet.Create((int)Protocol.G_TO_C_PART_COLLECTED, PlayerId.Value);
            var partMsg = new G_TO_C_PART_COLLECTED
            {
                PartId = extraCollectResult.Part.PartId,
                PartNameKr = extraCollectResult.Part.PartNameKr,
                PartTier = (int)extraCollectResult.Part.PartTier,
                StaminaReward = extraCollectResult.StaminaReward
            };
            partPacket.SetBody(MessagePackSerializer.Serialize(partMsg));
            Send(partPacket);

            using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = extraCollectResult.Part.PartId,
                StaminaReward = extraCollectResult.StaminaReward,
                NextTargetArea = 0,
                NextTargetInteractId = 0,
                NextTargetActionId = 0
            };
            stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
            Send(stepPacket);

            _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
                $"RNG 추가 부품 회수: {extraCollectResult.Part.PartNameKr}", isBot: false);

            StoreTrace((AreaType)info.ZoneId, msg.InteractId,
                GetMissionCollectTraceDescription(extraCollectResult.CompletedMissionNodeIds), true);
        }

        if (outcome.AddedInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedInventoryItem);
        foreach (var extraInventoryItem in outcome.AddedExtraInventoryItems)
            SendInGameInventoryUpdate(extraInventoryItem);
        if (outcome.AddedBonusInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedBonusInventoryItem);
        TryCompleteInteractObjectChecklist(info);

        Logger.LogInformation(
            "RNG 채집 FINISH: PlayerId={PlayerId}, InteractId={InteractId}, ResultType={Type}, ItemId={ItemId}, BonusItemId={BonusItemId}",
            PlayerId, msg.InteractId, outcome.ResultType, outcome.ItemId, outcome.BonusItemId);

        SendRngCollectResult(msg.InteractId, outcome.ResultType, outcome.ItemId,
            outcome.StaminaReward, outcome.CooldownSeconds);

        if (otherGiftDiscovery != null)
            SendGiftDiscovered(otherGiftDiscovery, 0);
        else
            CheckGiftDiscovery(msg.InteractId);

        // FINISH 시점에 30초 cooldown 갱신 broadcast (RngCollectCore.Resolve 내부에서 SetCooldown 30 호출됨)
        BroadcastRngCollectCooldown(msg.InteractId, outcome.CooldownSeconds);
        // IDLE 상태 broadcast — 같은 영역 모든 클라(본인 포함)가 받아 Player.Info.State 갱신.
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        return Task.CompletedTask;
    }

    private int ApplyDutyStaminaSaverToCost(int baseCost)
    {
        if (!PlayerId.HasValue || baseCost <= 0) return baseCost;
        if (!_missionManager.TryConsumeShortRewardUse(
                CurrentMapSubId,
                PlayerId.Value,
                MissionShortRewardType.DutyStaminaSaver,
                out var reward) || reward == null)
        {
            return baseCost;
        }

        int reduction = Math.Max(1, (int)Math.Ceiling(baseCost * reward.ValuePercent / 100.0));
        int adjustedCost = Math.Max(0, baseCost - reduction);

        Logger.LogInformation(
            "업무 체력 보존 적용: PlayerId={PlayerId}, BaseCost={BaseCost}, AdjustedCost={AdjustedCost}, RemainingUses={RemainingUses}",
            PlayerId, baseCost, adjustedCost, reward.RemainingUses);

        return adjustedCost;
    }

    private bool HasAvailableMissionActionTarget(InteractableInfoData info)
    {
        if (!PlayerId.HasValue) return false;

        var state = _missionManager.GetState(CurrentMapSubId, PlayerId.Value);
        if (state == null || state.IsCompleted) return false;

        lock (state.SyncRoot)
        {
            return GameMissionGraphData.GetAvailableNodes(
                    (short)state.JobTitle,
                    state.CollectedParts,
                    state.CompletedMissionNodeIds,
                    state.OwnedClueTags,
                    state.HasLostTarget)
                .Any(node =>
                    node.NodeKind != MissionGraphNodeKind.CollectPart &&
                    !state.CompletedMissionNodeIds.Contains(node.NodeId) &&
                    !IsStoryletLost(state, node) &&
                    node.MatchesInteractable(info.ZoneId, (int)info.ObjectType, info.Id));
        }
    }

    private static bool IsStoryletLost(PlayerPartState state, MissionGraphNodeData node) =>
        node.HasStoryletMetadata &&
        !string.IsNullOrWhiteSpace(node.EffectiveStoryletId) &&
        state.LostStoryletIds.Contains(node.EffectiveStoryletId);

    private bool TryHandleGiftDiscoveryBeforeCollect(int interactId, out GiftDiscoveryResult? otherGiftDiscovery)
    {
        otherGiftDiscovery = null;
        if (!PlayerId.HasValue) return false;
        if (!_missionManager.TryDiscoverGift(CurrentMapSubId, PlayerId.Value, interactId, out var result)) return false;

        if (result.DiscoveryType == GiftDiscoveryType.Other)
        {
            otherGiftDiscovery = result;
            return false;
        }

        var receivedGift = _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, result.ItemId, 1,
            GiftState.Received);
        SendInGameInventoryUpdate(receivedGift);

        ModifyStats(corruptionDelta: GiftFoundCorruptionDelta);
        SendGiftDiscovered(result, GiftFoundCorruptionDelta);
        SendGiftProgressToOwner(result);

        SendRngCollectResult(interactId, RngCollectGiftResultType, result.ItemId, 0, 0);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);

        CheckResourceElimination();
        return true;
    }

    /// <summary>
    ///     같은 영역 모든 클라(본인 포함)에 G_TO_C_PLAYER_STATE broadcast.
    /// </summary>
    private void BroadcastPlayerState(global::network.common.PlayerState state)
    {
        if (!PlayerId.HasValue) return;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.Send(packet);
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

    private void SendInteractCooldownSnapshot()
    {
        if (!PlayerId.HasValue) return;

        var snapshot = RngCollectCooldownStore.GetSnapshot(CurrentMapSubId);
        if (snapshot.Count == 0) return;

        var msg = new G_TO_C_INTERACT_COOLDOWN_SNAPSHOT
        {
            Entries = snapshot
                .Select(entry => new InteractCooldownSnapshotEntry
                {
                    InteractId = entry.InteractId,
                    RemainSeconds = entry.RemainingSeconds
                })
                .ToList()
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACT_COOLDOWN_SNAPSHOT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);

        Logger.LogInformation("Interact cooldown snapshot sent: PlayerId={PlayerId}, Count={Count}",
            PlayerId.Value, msg.Entries.Count);
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
