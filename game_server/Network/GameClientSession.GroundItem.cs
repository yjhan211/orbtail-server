using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private const int SurvivorInventorySlotCount = 6;

    private Task HandleGroundItemPickup(C_TO_G_GROUND_ITEM_PICKUP msg)
    {
        if (!PlayerId.HasValue || IsEliminated || _lastValidatedPosition == null)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        InGameItemInfo? addedItem = null;
        bool autoUsed = false;
        int staminaRecovery = 0;
        int corruptionRecovery = 0;
        ErrorCode rejection = ErrorCode.INVENTORY_FULL;
        var position = _lastValidatedPosition;
        var status = _groundItemManager.TryClaim(
            CurrentMapSubId,
            msg.GroundItemUid,
            PlayerId.Value,
            CurrentArea,
            position.X,
            position.Y,
            item =>
            {
                var disposition = GroundItemPickupPolicy.Resolve(
                    item.ItemId,
                    Stamina,
                    MaxStamina,
                    Corruption,
                    out staminaRecovery,
                    out corruptionRecovery);
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

                bool added = _inGameInventoryManager.TryAddItemWithCapacity(
                    CurrentMapSubId, PlayerId.Value, item.ItemId, SurvivorInventorySlotCount, out addedItem);
                if (!added) rejection = ErrorCode.INVENTORY_FULL;
                return added;
            },
            out var claimedItem);

        if (status != GroundItemClaimStatus.Success || claimedItem == null)
        {
            ErrorCode error = status switch
            {
                GroundItemClaimStatus.AreaMismatch => ErrorCode.AREA_MISMATCH,
                GroundItemClaimStatus.TooFar => ErrorCode.INVALID_POSITION,
                GroundItemClaimStatus.Rejected => rejection,
                GroundItemClaimStatus.SourceBlocked => ErrorCode.INVALID_GAME_STATE,
                _ => ErrorCode.ITEM_NOT_FOUND
            };
            SendGroundItemPickupResult(msg.GroundItemUid, claimedItem?.ItemId ?? 0, false, false, error);
            return Task.CompletedTask;
        }

        if (autoUsed)
            ModifyStats(staminaDelta: staminaRecovery, corruptionDelta: -corruptionRecovery);
        else if (addedItem != null)
            SendInGameInventoryUpdate(addedItem);

        BroadcastGroundItemRemoved(claimedItem, autoUsed);
        SendGroundItemPickupResult(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        return Task.CompletedTask;
    }

    private Task HandleDropGroundItem(C_TO_G_DROP_GROUND_ITEM msg)
    {
        if (!PlayerId.HasValue || IsEliminated || _lastValidatedPosition == null || CurrentArea == AreaType.None)
        {
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "Cannot drop an item in the current state");
            return Task.CompletedTask;
        }

        var item = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value).GetItem(msg.ItemUid);
        if (item == null || item.Count <= 0 ||
            !_inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid, 1, out var updated))
        {
            SendErrorResponse(ErrorCode.ITEM_NOT_FOUND, "Item is not in the inventory");
            return Task.CompletedTask;
        }

        if (updated != null) SendInGameInventoryUpdate(updated);
        var position = _lastValidatedPosition;
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, CurrentArea,
            position.X, position.Y, [item.ItemId], PlayerId.Value);
        BroadcastGroundItemsSpawned(CurrentArea, spawned);
        return Task.CompletedTask;
    }

    internal void DropAllInventoryAtCurrentPosition()
    {
        if (!PlayerId.HasValue || _lastValidatedPosition == null || CurrentArea == AreaType.None) return;

        var removed = _inGameInventoryManager.TakeAllItems(CurrentMapSubId, PlayerId.Value);
        if (removed.Count == 0) return;

        var itemIds = removed.SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count)).ToList();
        foreach (var item in removed)
            SendInGameInventoryUpdate(new InGameItemInfo
            {
                ItemUid = item.ItemUid,
                ItemId = item.ItemId,
                Count = 0,
                GiftState = item.GiftState
            });

        var position = _lastValidatedPosition;
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, CurrentArea,
            position.X, position.Y, itemIds);
        BroadcastGroundItemsSpawned(CurrentArea, spawned);
    }

    internal void DropBotInventoryAtCurrentPosition(long botPlayerId)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null || bot.CurrentArea == AreaType.None) return;

        var removed = _inGameInventoryManager.TakeAllItems(CurrentMapSubId, botPlayerId);
        if (removed.Count == 0) return;

        var itemIds = removed.SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count)).ToList();
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, bot.CurrentArea,
            bot.Position.X, bot.Position.Y, itemIds);
        BroadcastGroundItemsSpawned(bot.CurrentArea, spawned);
    }
    private void SendGroundItemSnapshot(AreaType area)
    {
        if (CurrentMapSubId <= 0 || area == AreaType.None) return;
        var items = _groundItemManager.GetSnapshot(CurrentMapSubId, area);
        int remaining = _areaItemStockManager.GetRemainingCount(CurrentMapSubId, (int)area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, remaining, items);
        Send(packet);
    }

    private void SpawnGroundItemsFromExplore(InteractableInfoData info, RngCollectOutcome outcome)
    {
        if (outcome.DroppedItemIds.Count == 0) return;

        var origin = CellToWorldPosition(new Cell(info.CellX, info.CellY));
        var area = (AreaType)info.ZoneId;
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, area,
            origin.X, origin.Y, outcome.DroppedItemIds);
        BroadcastGroundItemsSpawned(area, spawned);
    }

    private void BroadcastGroundItemsSpawned(AreaType area, IReadOnlyList<GroundItemInfo> spawned)
    {
        if (spawned.Count == 0) return;
        int remaining = _areaItemStockManager.GetRemainingCount(CurrentMapSubId, (int)area);
        var sessions = GetSessionsInArea(
            _getSessionsByInstance(CurrentMapId, CurrentMapSubId), area, excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, spawned.ToList());
        foreach (var session in sessions) session.Send(packet);
    }

    private void BroadcastGroundItemRemoved(GroundItemInfo item, bool autoUsed)
    {
        var sessions = GetSessionsInArea(
            _getSessionsByInstance(CurrentMapId, CurrentMapSubId),
            (AreaType)item.AreaType,
            excludeSelf: false);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(item.GroundItemUid, PlayerId ?? 0, autoUsed);
        foreach (var session in sessions) session.Send(packet);
    }

    private void SendGroundItemPickupResult(long groundItemUid, int itemId, bool success, bool autoUsed,
        ErrorCode errorCode)
    {
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(
            groundItemUid, itemId, success, autoUsed, errorCode);
        Send(packet);
    }
}