using System;
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
    private static readonly bool MissionActionCollectBlockingEnabled = false;
    private const int RngCollectGiftResultType = 5;
    private const int RngCollectEncounterResultType = 6;
    private const int RngCollectItemResultType = 2;
    private const int RngCollectStaminaCost = 5;

    // #217 P0-c: 스웜 아레나 탐색 스팟 — 비용·리젠 규칙은 봇과 공유하므로 Config에 있다.
    private const int SwarmExploreCooldownSeconds = Config.SWARM_EXPLORE_REGEN_SECONDS;

    /// <summary>개봉 비용은 장소에 붙는다: 기본가 + 그 스팟의 재개봉 가산.</summary>
    private int GetSwarmExploreCost(int interactId) =>
        Config.GetSwarmExploreCost(
            RngCollectCooldownStore.GetOpenCount(CurrentMapSubId, interactId));

    /// <summary>START 처리됐으나 FINISH 대기 중인 InteractId — 매칭 단위 추적.
    /// FINISH 도착 시 이 set에 있어야 결과 산출 진행.</summary>
    private static readonly TimeSpan RngCollectPendingEncounterBlockDuration = TimeSpan.FromSeconds(10);
    private readonly HashSet<int> _pendingFinish = new();
    private DateTime _rngCollectPendingEncounterBlockUntilUtc = DateTime.MinValue;

    private Task HandleRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (Config.SWARM_P0_ENABLED)
            return HandleSwarmRngCollectStart(msg);
        if (Config.SPOT_ARENA_P0_ENABLED)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }
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
        if (_pendingRoomEntryEventId != 0)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            ResendPendingRoomEntryEvent("RngCollectStartBlocked");
            return Task.CompletedTask;
        }

        CancelPendingRoomEncounterTurnsForCurrentPlayer("RngCollectStart");

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            ClearRoomEncounterStartCandidates(msg.InteractId);
            Logger.LogWarning("RNG START InteractId 미존재: {InteractId}", msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (info.ZoneId != (int)CurrentArea)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.AREA_MISMATCH, 0);
            return Task.CompletedTask;
        }

        if (!_areaItemStockManager.HasRemaining(CurrentMapSubId, info.ZoneId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INTERACTABLE_NOT_AVAILABLE, 0);
            return Task.CompletedTask;
        }
        if (MissionActionCollectBlockingEnabled && HasAvailableMissionActionTarget(info))
        {
            Logger.LogDebug("RNG START mission action target blocked: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.INTERACTABLE_NOT_AVAILABLE, 0);
            return Task.CompletedTask;
        }

        if (!RngCollectCooldownStore.TryAcquireCooldown(
                CurrentMapSubId, msg.InteractId, RngCollectCooldownStore.DefaultCooldownSeconds, out int remaining))
        {
            Logger.LogDebug(
                "RNG START cooldown rejected: PlayerId={PlayerId}, InteractId={InteractId}, Remaining={Remaining}s",
                PlayerId, msg.InteractId, remaining);
            SendRngCollectAck(msg.InteractId, ErrorCode.ACTION_ALREADY_EXPLORED, remaining);
            return Task.CompletedTask;
        }

        int staminaCost = ApplyDutyStaminaSaverToCost(RngCollectStaminaCost);

        // stamina 차감 (즉시) — 정신력 1:2 변환은 ModifyStats가 처리
        ModifyStats(-staminaCost);

        // 사보타주 상태 자동 복구
        var currentState = _interactableStateManager.GetInteractableState(CurrentMapSubId, msg.InteractId);
        if (currentState == (int)InteractableStateType.SABOTAGE)
            _sabotageManager.OnActionCompleted(CurrentMapSubId, msg.InteractId, 0);

        SnapshotRoomEncounterStartCandidates(msg.InteractId, info);
        _pendingFinish.Add(msg.InteractId);
        MarkRngCollectPendingEncounterBlock();

        Logger.LogInformation("RNG collect START: PlayerId={PlayerId}, InteractId={InteractId}",
            PlayerId, msg.InteractId);
        _gameEventLogManager.LogExploreStart(
            CurrentMapSubId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);

        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
        return Task.CompletedTask;
    }

    private Task HandleRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (Config.SWARM_P0_ENABLED)
            return HandleSwarmRngCollectFinish(msg);
        if (Config.SPOT_ARENA_P0_ENABLED) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        if (msg.EncounterCheckOnly)
        {
            if (!_pendingFinish.Contains(msg.InteractId))
            {
                ClearRoomEncounterStartCandidates(msg.InteractId);
                Logger.LogWarning(
                    "RNG encounter check without START or duplicated: PlayerId={PlayerId}, InteractId={InteractId}",
                    PlayerId, msg.InteractId);
                SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
                return Task.CompletedTask;
            }

            var checkInfo = GameInteractableData.Get(msg.InteractId);
            if (checkInfo == null)
            {
                _pendingFinish.Remove(msg.InteractId);
                ClearRngCollectPendingEncounterBlockIfIdle();
                ClearRoomEncounterStartCandidates(msg.InteractId);
                Logger.LogWarning("RNG encounter check InteractId missing: {InteractId}", msg.InteractId);
                SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
                return Task.CompletedTask;
            }

            if (TryHandleRoomEncounterFromExploreSpot(msg.InteractId, checkInfo))
            {
                _pendingFinish.Remove(msg.InteractId);
                ClearRngCollectPendingEncounterBlockIfIdle();
                Logger.LogInformation(
                    "RNG arrival room encounter resolved at explore spot: PlayerId={PlayerId}, InteractId={InteractId}",
                    PlayerId, msg.InteractId);
                return Task.CompletedTask;
            }

            SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
            return Task.CompletedTask;
        }

        if (!_pendingFinish.Remove(msg.InteractId))
        {
            ClearRoomEncounterStartCandidates(msg.InteractId);
            Logger.LogWarning(
                "RNG FINISH without START or duplicated: PlayerId={PlayerId}, InteractId={InteractId}",
                PlayerId, msg.InteractId);
            // 무응답으로 두면 클라이언트가 EXPLORE_1 채집 상태에 박제된다 — 에러 ACK로 정리를 유도
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        ClearRngCollectPendingEncounterBlockIfIdle();

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            ClearRoomEncounterStartCandidates(msg.InteractId);
            Logger.LogWarning("RNG FINISH InteractId missing: {InteractId}", msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        ClearRoomEncounterStartCandidates(msg.InteractId);

        if (TryStartRoomExploreEvent((AreaType)info.ZoneId, msg.InteractId))
            return Task.CompletedTask;

        bool allowGiftDiscovery = AllowsGiftDiscoveryOnCollect(info, CurrentArea);
        GiftDiscoveryResult? otherGiftDiscovery = null;
        if (allowGiftDiscovery && TryHandleGiftDiscoveryBeforeCollect(msg.InteractId, out otherGiftDiscovery))
            return Task.CompletedTask;

        var outcome = RngCollectCore.Resolve(
            matchingId: CurrentMapSubId,
            playerId: PlayerId.Value,
            jobTitle: MyJobTitle,
            info: info,
            missionManager: _missionManager,
            inventoryManager: _inGameInventoryManager,
            itemPoolManager: _itemPoolManager,
            areaItemStockManager: _areaItemStockManager,
            isBot: false,
            bonusItemChancePercent: PassiveBuffUtility.GetValuePercent(
                ActiveBuffIds,
                BuffSubType.ITEM_GAIN_CHANCE_ADD));

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

        int clientResultType = outcome.ResultType;
        int clientItemId = outcome.ItemId;
        if (IsEmptyRngCollectResult(clientResultType) && outcome.BonusItemId > 0)
        {
            clientResultType = RngCollectItemResultType;
            clientItemId = outcome.BonusItemId;
        }

        Logger.LogInformation(
            "RNG 채집 FINISH: PlayerId={PlayerId}, InteractId={InteractId}, ResultType={Type}, ClientResultType={ClientType}, ItemId={ItemId}, ClientItemId={ClientItemId}, BonusItemId={BonusItemId}",
            PlayerId, msg.InteractId, outcome.ResultType, clientResultType, outcome.ItemId, clientItemId, outcome.BonusItemId);

        SendRngCollectResult(msg.InteractId, clientResultType, clientItemId,
            outcome.StaminaReward, outcome.CooldownSeconds);
        SpawnGroundItemsFromExplore(info, outcome);
        BroadcastSurvivorAreaStockState();

        _gameEventLogManager.LogExploreCompleted(
            CurrentMapSubId,
            PlayerId.Value,
            msg.InteractId,
            CurrentArea.ToString(),
            outcome.DroppedItemIds,
            _areaItemStockManager.GetRemainingCount(CurrentMapSubId, (int)CurrentArea),
            isBot: false);
        if (outcome.AddedInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedInventoryItem);
        foreach (var extraInventoryItem in outcome.AddedExtraInventoryItems)
            SendInGameInventoryUpdate(extraInventoryItem);
        if (outcome.AddedBonusInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedBonusInventoryItem);
        TryCompleteInteractObjectChecklist(info);

        if (allowGiftDiscovery)
        {
            if (otherGiftDiscovery != null)
                SendGiftDiscovered(otherGiftDiscovery, 0);
            else
                CheckGiftDiscovery(msg.InteractId);
        }

        // FINISH 시점에 30초 cooldown 갱신 broadcast (RngCollectCore.Resolve 내부에서 SetCooldown 30 호출됨)
        BroadcastRngCollectCooldown(msg.InteractId, outcome.CooldownSeconds);
        // IDLE 상태 broadcast — 같은 영역 모든 클라(본인 포함)가 받아 Player.Info.State 갱신.
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        ResolvePendingRoomDiscoveriesAfterExploreFinished((AreaType)info.ZoneId);
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

        ModifyStats(corruptionDelta: GiftFoundCorruptionDelta);
        SendRngCollectResult(interactId, RngCollectGiftResultType, result.ItemId, 0, 0);
        SendInGameInventoryUpdate(receivedGift);
        SendGiftDiscovered(result, GiftFoundCorruptionDelta);
        SendGiftProgressToOwner(result);
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
        BroadcastRngCollectCooldown(interactId, 0);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        ResolvePendingRoomDiscoveriesAfterExploreFinished(CurrentArea);

        CheckResourceElimination();
        return true;
    }

    /// <summary>
    ///     같은 영역 모든 클라(본인 포함)에 G_TO_C_PLAYER_STATE broadcast.
    /// </summary>
    private void BroadcastPlayerState(global::network.common.PlayerState state)
    {
        if (!PlayerId.HasValue) return;
        CurrentState = state == global::network.common.PlayerState.EXPLORE_1
            ? PlayerState.Exploring
            : PlayerState.Idle;
        _exploreMoveGraceUntil = CurrentState == PlayerState.Exploring
            ? DateTime.UtcNow + ExploreMoveGracePeriod
            : DateTime.MinValue;

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, state);
        foreach (var session in sameAreaSessions) session.Send(packet);
    }

    /// <summary>
    ///     #217 P0-c: 스웜 아레나에서 탐색 오브젝트는 소환석 5개짜리 오브 드래프트 상자다.
    ///     기존 자동탐색 UX(접근 → 게이지 → 완료)를 그대로 쓰고, 완료 시 오브를 소환한다.
    ///     스태미나·미션·선물·조우 등 레거시 채집 결과는 사용하지 않는다.
    /// </summary>
    private Task HandleSwarmRngCollectStart(C_TO_G_RNG_COLLECT_START msg)
    {
        if (IsEliminated)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (info.ZoneId != (int)CurrentArea)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.AREA_MISMATCH, 0);
            return Task.CompletedTask;
        }

        // 소환석 부족이면 게이지를 시작하지 않는다 — 헛 채널 방지.
        if (_summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId!.Value).StoneCount <
            GetSwarmExploreCost(msg.InteractId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INSUFFICIENT_CURRENCY, 0);
            return Task.CompletedTask;
        }

        // 궤도 포화도 시작 전에 막는다 — FINISH의 소환 실패가 쿨다운을 되돌리는 설계와
        // 클라 자동 수집이 맞물리면 1.5초 주기 무한 재수집 루프가 된다 (2026-08-07 보건실 관측).
        // 판정은 TryAddItemWithCapacity와 동일한 슬롯 수 기준.
        if (_inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value)
                .GetAllItems().Count >= Config.SWARM_ORB_CAPACITY)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVENTORY_FULL, 0);
            return Task.CompletedTask;
        }

        if (!RngCollectCooldownStore.TryAcquireCooldown(
                CurrentMapSubId, msg.InteractId, RngCollectCooldownStore.DefaultCooldownSeconds,
                out int remaining))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.ACTION_ALREADY_EXPLORED, remaining);
            return Task.CompletedTask;
        }

        _pendingFinish.Add(msg.InteractId);
        _gameEventLogManager.LogExploreStart(
            CurrentMapSubId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);
        SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);

        // 개봉 소음 — 주변 스웜이 개봉자에게 몰린다. 게이지가 곧 리스크 창.
        SwarmExploreNoiseCallback?.Invoke(CurrentMapSubId, PlayerId.Value);
        return Task.CompletedTask;
    }

    private Task HandleSwarmRngCollectFinish(C_TO_G_RNG_COLLECT_FINISH msg)
    {
        if (msg.EncounterCheckOnly)
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.SUCCESS, 0);
            return Task.CompletedTask;
        }

        // 탈락 후 도착한 FINISH가 소환에 성공하면 드랍된 인벤토리와 상태가 꼬인다
        if (IsEliminated)
        {
            _pendingFinish.Remove(msg.InteractId);
            SendRngCollectAck(msg.InteractId, ErrorCode.FATAL, 0);
            return Task.CompletedTask;
        }

        if (!_pendingFinish.Remove(msg.InteractId))
        {
            SendRngCollectAck(msg.InteractId, ErrorCode.INVALID_GAME_STATE, 0);
            return Task.CompletedTask;
        }

        var attempt = ExecuteOrbSummon(choiceIndex: 0, costOverride: GetSwarmExploreCost(msg.InteractId));
        if (!attempt.Success)
        {
            // 소환 실패(석 부족·보드 포화) — 쿨다운을 풀어 나중에 다시 열 수 있게 한다.
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, msg.InteractId);
            BroadcastRngCollectCooldown(msg.InteractId, 0);
            SendRngCollectResult(msg.InteractId, 0, 0, 0, 0);
            BroadcastPlayerState(global::network.common.PlayerState.IDLE);
            return Task.CompletedTask;
        }

        // 스팟은 소진되지 않는다 — 리젠 시간 뒤 재개봉 가산이 붙어 다시 나온다.
        RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, msg.InteractId);
        RngCollectCooldownStore.TryAcquireCooldown(
            CurrentMapSubId, msg.InteractId, SwarmExploreCooldownSeconds, out _);
        RngCollectCooldownStore.IncrementOpenCount(CurrentMapSubId, msg.InteractId);
        BroadcastRngCollectCooldown(msg.InteractId, SwarmExploreCooldownSeconds);
        SendRngCollectResult(
            msg.InteractId, RngCollectItemResultType, attempt.ItemId, 0, SwarmExploreCooldownSeconds);
        BroadcastPlayerState(global::network.common.PlayerState.IDLE);
        Logger.LogInformation(
            "Swarm explore summon: PlayerId={PlayerId}, InteractId={InteractId}, ItemId={ItemId}",
            PlayerId, msg.InteractId, attempt.ItemId);

        // 자동 머지: 개봉으로 쌍이 생기면 즉시 합성 — 보드 관리를 실시간에서 제거한다.
        foreach (var mergedItem in _inGameInventoryManager.AutoMergeSurvivorOrbs(
                     CurrentMapSubId, PlayerId.Value, Random.Shared))
            SendInGameInventoryUpdate(mergedItem);

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

    private void MarkRngCollectPendingEncounterBlock()
    {
        _rngCollectPendingEncounterBlockUntilUtc = DateTime.UtcNow + RngCollectPendingEncounterBlockDuration;
    }

    private void ClearRngCollectPendingEncounterBlockIfIdle()
    {
        if (_pendingFinish.Count == 0)
            _rngCollectPendingEncounterBlockUntilUtc = DateTime.MinValue;
    }

    private void CancelPendingRngCollect(string reason)
    {
        if (_pendingFinish.Count == 0)
            return;

        foreach (int interactId in _pendingFinish.ToArray())
        {
            _gameEventLogManager.LogExploreCancelled(
                CurrentMapSubId, PlayerId.GetValueOrDefault(), interactId, CurrentArea.ToString(), reason, isBot: false);
            ClearRoomEncounterStartCandidates(interactId);
            RngCollectCooldownStore.ClearCooldown(CurrentMapSubId, interactId);
            BroadcastRngCollectCooldown(interactId, 0);
        }

        Logger.LogInformation(
            "RNG collect pending cancelled: PlayerId={PlayerId}, Count={Count}, Reason={Reason}",
            PlayerId, _pendingFinish.Count, reason);
        _pendingFinish.Clear();
        ClearRngCollectPendingEncounterBlockIfIdle();
    }

    private bool HasPendingRngCollectEncounterBlock(DateTime now)
    {
        return _pendingFinish.Count > 0 && now <= _rngCollectPendingEncounterBlockUntilUtc;
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

    private static bool IsEmptyRngCollectResult(int resultType)
    {
        return resultType == 0 || resultType == 1;
    }

    private static bool AllowsGiftDiscoveryOnCollect(InteractableInfoData info, AreaType currentArea)
    {
        return currentArea != AreaType.BroadcastRoom && (AreaType)info.ZoneId != AreaType.BroadcastRoom;
    }
}
