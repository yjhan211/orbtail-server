using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandleGroundItemPickup(C_TO_G_GROUND_ITEM_PICKUP msg)
    {
        if (!PlayerId.HasValue)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }
        if (MatchingId <= 0)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        return RunUnderMatch(
            () => HandleGroundItemPickupCore(msg),
            () => SendGroundItemPickupResult(
                msg.GroundItemUid,
                0,
                false,
                false,
                ErrorCode.INVALID_GAME_STATE));
    }

    private Task HandleGroundItemPickupCore(C_TO_G_GROUND_ITEM_PICKUP msg)
    {
        if (!PlayerId.HasValue || IsEliminated || IsGameEnded || _lastValidatedPosition == null)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        InGameItemInfo? addedItem = null;
        bool autoUsed = false;
        bool autoEquipped = false;
        bool summonStonePickup = false;
        bool jamPickup = false;
        bool bootsPickup = false;
        bool keyPickup = false;
        int staminaRecovery = 0;
        int corruptionRecovery = 0;
        ErrorCode rejection = ErrorCode.INVENTORY_FULL;
        var position = _lastValidatedPosition;
        long discovererPlayerId = _matchRuntimes.GetRequired(MatchingId).GroundItems.GetDiscovererPlayerId(
            msg.GroundItemUid);
        var attemptedItem = _matchRuntimes.GetRequired(MatchingId).GroundItems.GetItem(msg.GroundItemUid);
        var status = _matchRuntimes.GetRequired(MatchingId).GroundItems.TryClaim(
            msg.GroundItemUid,
            PlayerId.Value,
            CurrentArea,
            position.X,
            position.Y,
            item =>
            {
                if (item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
                {
                    summonStonePickup = true;
                    return true;
                }

                if (item.ItemId == Config.JAM_GROUND_ITEM_ID)
                {
                    jamPickup = true;
                    return true;
                }

                if (item.ItemId == Config.BOOTS_GROUND_ITEM_ID)
                {
                    bootsPickup = true;
                    return true;
                }

                if (item.ItemId == Config.KEY_GROUND_ITEM_ID)
                {
                    keyPickup = true;
                    return true;
                }

                var disposition = GroundItemPickupPolicy.Resolve(
                    item.ItemId,
                    Stamina,
                    MaxStamina,
                    Corruption,
                    out staminaRecovery,
                    out corruptionRecovery,
                    MatchingId,
                    PlayerId.Value);
                if (disposition == GroundItemPickupDisposition.LeaveOnGround)
                {
                    rejection = ErrorCode.ITEM_NOT_USABLE;
                    return false;
                }
                if (disposition == GroundItemPickupDisposition.AutoUse)
                {
                    autoUsed = true;
                    return true;
                }

                bool added = _matchRuntimes.GetRequired(MatchingId).Inventory.TryAddItemWithCapacity(
                    PlayerId.Value, item.ItemId, Config.GetOrbCapacity(),
                    out addedItem);
                if (!added) rejection = ErrorCode.INVENTORY_FULL;
                else if (addedItem != null)
                {
                    var equippedItem = _matchRuntimes.GetRequired(MatchingId).Inventory.GetEquippedBattleItem(
                        PlayerId.Value);
                    autoEquipped = equippedItem?.ItemUid == addedItem.ItemUid;
                }
                return added;
            },
            out var claimedItem);

        if (status != GroundItemClaimStatus.Success || claimedItem == null)
        {
            if (attemptedItem?.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
            {
                Logger.LogDebug(
                    "Summon stone pickup rejected: MatchingId={MatchingId}, PlayerId={PlayerId}, GroundItemUid={GroundItemUid}, Status={Status}, PlayerArea={PlayerArea}, ItemArea={ItemArea}, Player=({PlayerX:F2},{PlayerY:F2}), Item=({ItemX:F2},{ItemY:F2})",
                    MatchingId,
                    PlayerId.Value,
                    msg.GroundItemUid,
                    status,
                    CurrentArea,
                    attemptedItem.AreaType,
                    position.X,
                    position.Y,
                    attemptedItem.PositionX,
                    attemptedItem.PositionY);
            }

            ErrorCode error = status switch
            {
                GroundItemClaimStatus.AreaMismatch => ErrorCode.AREA_MISMATCH,
                GroundItemClaimStatus.TooFar => ErrorCode.INVALID_POSITION,
                GroundItemClaimStatus.Rejected => rejection,
                GroundItemClaimStatus.SourceBlocked => ErrorCode.INVALID_GAME_STATE,
                GroundItemClaimStatus.Reserved => ErrorCode.ITEM_NOT_FOUND,
                _ => ErrorCode.ITEM_NOT_FOUND
            };
            if (attemptedItem != null && GroundItemPickupPolicy.IsImmediateUseItem(attemptedItem.ItemId))
            {
                GroundItemPickupPolicy.Resolve(attemptedItem.ItemId, Stamina, MaxStamina, Corruption,
                    out int deniedStaminaRecovery, out int deniedCorruptionRecovery);
                _gameEventLogManager.LogPelletPickupOutcome(
                    MatchingId, PlayerId.Value, attemptedItem.ItemId,
                    deniedStaminaRecovery + deniedCorruptionRecovery, 0,
                    $"denied_{status.ToString().ToLowerInvariant()}", isBot: false);
            }
            if (attemptedItem != null && error == ErrorCode.INVENTORY_FULL)
            {
                var board = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
                _gameEventLogManager.LogOrbPickupBlockedFull(
                    MatchingId,
                    PlayerId.Value,
                    attemptedItem.ItemId,
                    CurrentArea.ToString(),
                    board.GetAllItems(),
                    isBot: false);
            }
            SendGroundItemPickupResult(msg.GroundItemUid, attemptedItem?.ItemId ?? 0, false, false, error);
            return Task.CompletedTask;
        }

        if (jamPickup)
        {
            AddJam(1);
        }
        else if (bootsPickup)
        {
            // 부츠 (#222 M4): 이속은 클라 이동이 소유한다 — 서버는 픽업 결과만 확정.
            // 클라가 픽업 결과(ItemId)로 10초 버프·HUD 타이머를 시작한다.
        }
        else if (keyPickup)
        {
            AddFreeSummonCharge(1);
        }
        else if (summonStonePickup)
        {
            var summonState = _matchRuntimes.GetRequired(MatchingId).SummonStones.AddStones(PlayerId.Value, 1);
            SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
            _gameEventLogManager.LogSummonStoneAward(
                MatchingId,
                PlayerId.Value,
                monsterId: 0,
                amount: 1,
                summonState.StoneCount,
                CurrentArea.ToString(),
                isCore: false,
                isBot: false);
        }
        else if (autoUsed)
        {
            int effectiveStaminaRecovery = Math.Min(staminaRecovery, Math.Max(0, MaxStamina - Stamina));
            int effectiveCorruptionRecovery = Math.Min(corruptionRecovery, Math.Max(0, Corruption));
            int requestedRecovery = staminaRecovery + corruptionRecovery;
            int effectiveRecovery = effectiveStaminaRecovery + effectiveCorruptionRecovery;
            ModifyStats(staminaDelta: staminaRecovery, corruptionDelta: -corruptionRecovery);
            // 하트는 앞줄 오브 HP도 만충으로 (#222 M4) — 원작 하트의 스쿼드 회복.
            if (claimedItem.ItemId == Config.HEART_GROUND_ITEM_ID)
                SwarmHeartPickupCallback?.Invoke(MatchingId, PlayerId.Value);
            _gameEventLogManager.LogRecoveryUse(
                MatchingId, PlayerId.Value, claimedItem.ItemId,
                effectiveRecovery, source: "ground_auto_use", isBot: false);
            _gameEventLogManager.LogPelletPickupOutcome(
                MatchingId, PlayerId.Value, claimedItem.ItemId, requestedRecovery, effectiveRecovery,
                effectiveRecovery == 0 ? "wasted" : effectiveRecovery == requestedRecovery ? "effective" : "partial_waste",
                isBot: false);
        }
        else if (addedItem != null)
        {
            SendInGameInventoryUpdate(addedItem);
            if (autoEquipped)
            {
                using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
                    true, addedItem.ItemUid, ErrorCode.SUCCESS);
                TrySend(equippedPacket);
            }
        }

        BroadcastGroundItemRemoved(claimedItem, autoUsed);
        _gameEventLogManager.LogGroundItemPickup(
            MatchingId,
            PlayerId.Value,
            discovererPlayerId,
            claimedItem.GroundItemUid,
            claimedItem.ItemId,
            CurrentArea.ToString(),
            autoUsed,
            isBot: false);
        if (!summonStonePickup && !jamPickup && !bootsPickup && !keyPickup)
        {
            var boardAfterPickup = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
            _gameEventLogManager.LogOrbBoardTransition(
                MatchingId, PlayerId.Value, boardAfterPickup.GetAllItems(),
                boardAfterPickup.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "pickup", isBot: false);
        }
        SendGroundItemPickupResult(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        return Task.CompletedTask;
    }

    /// <summary>잼 획득 (#222 M3) — 지갑 가산 + 상태 전송. 매치 시작 시 ResetJam으로 초기화.</summary>
    internal void AddJam(int amount)
    {
        if (!PlayerId.HasValue || amount <= 0)
            return;

        JamCount += amount;
        using var packet = Packet.Create((int)Protocol.G_TO_C_JAM_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_JAM_STATE { JamCount = JamCount }));
        TrySend(packet);
    }

    /// <summary>열쇠 (#222 M4): 무료 소환 충전 획득 — 상태를 소유자에게 즉시 동기한다.</summary>
    internal void AddFreeSummonCharge(int amount)
    {
        if (!PlayerId.HasValue || amount <= 0)
            return;

        FreeSummonCharges += amount;
        SendFreeSummonState();
    }

    internal void SendFreeSummonState()
    {
        if (!PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_FREE_SUMMON_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_FREE_SUMMON_STATE
        {
            Charges = FreeSummonCharges
        }));
        TrySend(packet);
    }

    internal void ResetJam(bool notify = false)
    {
        JamCount = 0;
        if (!notify || !PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_JAM_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_JAM_STATE { JamCount = 0 }));
        TrySend(packet);
    }

    private Task HandleDropGroundItem(C_TO_G_DROP_GROUND_ITEM msg)
    {
        // The six board slots are deliberate route pressure. Free floor drops would bypass that pressure.
        SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Direct board discard is unavailable");
        return Task.CompletedTask;
    }
    internal void DropAllInventoryAtCurrentPosition()
    {
        if (!PlayerId.HasValue || _lastValidatedPosition == null || CurrentArea == AreaType.None) return;

        var position = _lastValidatedPosition;
        var drop = EliminationInventoryDropper.DropAll(
            _matchRuntimes.GetRequired(MatchingId).Inventory,
            _matchRuntimes.GetRequired(MatchingId).GroundItems,
            MatchingId,
            PlayerId.Value,
            CurrentArea,
            position.X,
            position.Y,
            CurrentMapId);
        if (drop.RemovedItems.Count == 0) return;

        var emptyBoard = _matchRuntimes.GetRequired(MatchingId).Inventory.GetPlayerInventory(PlayerId.Value);
        _gameEventLogManager.LogOrbBoardTransition(
            MatchingId, PlayerId.Value, emptyBoard.GetAllItems(), 0, CurrentArea.ToString(), "elimination_drop",
            isBot: false);
        foreach (var item in drop.RemovedItems)
            SendInGameInventoryUpdate(new InGameItemInfo
            {
                ItemUid = item.ItemUid,
                ItemId = item.ItemId,
                Count = 0,
                GiftState = item.GiftState
            });

        if (drop.DroppedItemIds.Count == 0) return;

        _gameEventLogManager.LogEliminationDrop(
            MatchingId,
            PlayerId.Value,
            CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: false);
        BroadcastGroundItemsSpawned(CurrentArea, drop.SpawnedItems);
    }

    private void SendGroundItemSnapshot(AreaType area)
    {
        if (MatchingId <= 0 || area == AreaType.None) return;
        var items = _matchRuntimes.GetRequired(MatchingId).GroundItems.GetSnapshot(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, items.ToList());
        TrySend(packet);
    }


    private void BroadcastGroundItemsSpawned(AreaType area, IReadOnlyList<GroundItemInfo> spawned)
    {
        if (spawned.Count == 0) return;
        var sessions = GetSessionsInArea(
            _getSessionsByInstance(CurrentMapId, MatchingId), area, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, spawned.ToList());
        foreach (var session in sessions) session.TrySend(packet);
    }

    private void BroadcastGroundItemRemoved(GroundItemInfo item, bool autoUsed)
    {
        var sessions = GetSessionsInArea(
            _getSessionsByInstance(CurrentMapId, MatchingId),
            (AreaType)item.AreaType,
            excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(item.GroundItemUid, PlayerId ?? 0, autoUsed);
        foreach (var session in sessions) session.TrySend(packet);
    }

    private void SendGroundItemPickupResult(long groundItemUid, int itemId, bool success, bool autoUsed,
        ErrorCode errorCode)
    {
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(
            groundItemUid, itemId, success, autoUsed, errorCode);
        TrySend(packet);
    }
}
