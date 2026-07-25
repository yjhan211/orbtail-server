using game_server.services;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private Task HandleGroundItemPickup(C_TO_G_GROUND_ITEM_PICKUP msg)
    {
        if (!PlayerId.HasValue || IsEliminated || _lastValidatedPosition == null)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        InGameItemInfo? addedItem = null;
        bool autoUsed = false;
        bool autoEquipped = false;
        int staminaRecovery = 0;
        int corruptionRecovery = 0;
        ErrorCode rejection = ErrorCode.INVENTORY_FULL;
        var position = _lastValidatedPosition;
        long discovererPlayerId = _groundItemManager.GetDiscovererPlayerId(
            CurrentMapSubId, msg.GroundItemUid);
        var attemptedItem = _groundItemManager.GetItem(CurrentMapSubId, msg.GroundItemUid);
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
                    CurrentMapSubId, PlayerId.Value, item.ItemId, Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                    out addedItem);
                if (!added) rejection = ErrorCode.INVENTORY_FULL;
                else if (addedItem != null)
                {
                    var equippedItem = _inGameInventoryManager.GetEquippedBattleItem(
                        CurrentMapSubId, PlayerId.Value);
                    autoEquipped = equippedItem?.ItemUid == addedItem.ItemUid;
                }
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
                GroundItemClaimStatus.Reserved => ErrorCode.ITEM_NOT_FOUND,
                _ => ErrorCode.ITEM_NOT_FOUND
            };
            if (attemptedItem != null && GroundItemPickupPolicy.IsImmediateUseItem(attemptedItem.ItemId))
            {
                GroundItemPickupPolicy.Resolve(attemptedItem.ItemId, Stamina, MaxStamina, Corruption,
                    out int deniedStaminaRecovery, out int deniedCorruptionRecovery);
                _gameEventLogManager.LogPelletPickupOutcome(
                    CurrentMapSubId, PlayerId.Value, attemptedItem.ItemId,
                    deniedStaminaRecovery + deniedCorruptionRecovery, 0,
                    $"denied_{status.ToString().ToLowerInvariant()}", isBot: false);
            }
            if (attemptedItem != null && error == ErrorCode.INVENTORY_FULL)
            {
                var board = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                _gameEventLogManager.LogSurvivorOrbPickupBlockedFull(
                    CurrentMapSubId,
                    PlayerId.Value,
                    attemptedItem.ItemId,
                    CurrentArea.ToString(),
                    board.GetAllItems(),
                    isBot: false);
            }
            SendGroundItemPickupResult(msg.GroundItemUid, attemptedItem?.ItemId ?? 0, false, false, error);
            return Task.CompletedTask;
        }

        if (autoUsed)
        {
            int effectiveStaminaRecovery = Math.Min(staminaRecovery, Math.Max(0, MaxStamina - Stamina));
            int effectiveCorruptionRecovery = Math.Min(corruptionRecovery, Math.Max(0, Corruption));
            int requestedRecovery = staminaRecovery + corruptionRecovery;
            int effectiveRecovery = effectiveStaminaRecovery + effectiveCorruptionRecovery;
            ModifyStats(staminaDelta: staminaRecovery, corruptionDelta: -corruptionRecovery);
            _gameEventLogManager.LogRecoveryUse(
                CurrentMapSubId, PlayerId.Value, claimedItem.ItemId,
                effectiveRecovery, source: "ground_auto_use", isBot: false);
            _gameEventLogManager.LogPelletPickupOutcome(
                CurrentMapSubId, PlayerId.Value, claimedItem.ItemId, requestedRecovery, effectiveRecovery,
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
                Send(equippedPacket);
            }
        }

        BroadcastGroundItemRemoved(claimedItem, autoUsed);
        _gameEventLogManager.LogGroundItemPickup(
            CurrentMapSubId,
            PlayerId.Value,
            discovererPlayerId,
            claimedItem.GroundItemUid,
            claimedItem.ItemId,
            CurrentArea.ToString(),
            autoUsed,
            isBot: false);
        var boardAfterPickup = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            CurrentMapSubId, PlayerId.Value, boardAfterPickup.GetAllItems(),
            boardAfterPickup.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "pickup", isBot: false);
        SendGroundItemPickupResult(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        return Task.CompletedTask;
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

        var removed = _inGameInventoryManager.TakeAllItems(CurrentMapSubId, PlayerId.Value);
        if (removed.Count == 0) return;

        var emptyBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            CurrentMapSubId, PlayerId.Value, emptyBoard.GetAllItems(), 0, CurrentArea.ToString(), "elimination_drop",
            isBot: false);
        var itemIds = removed
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        foreach (var item in removed)
            SendInGameInventoryUpdate(new InGameItemInfo
            {
                ItemUid = item.ItemUid,
                ItemId = item.ItemId,
                Count = 0,
                GiftState = item.GiftState
            });

        if (itemIds.Count == 0) return;

        var position = _lastValidatedPosition;
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, CurrentArea,
            position.X, position.Y, itemIds, layout: GroundItemSpawnLayout.EliminationScatter);
        _gameEventLogManager.LogEliminationDrop(
            CurrentMapSubId,
            PlayerId.Value,
            CurrentArea.ToString(),
            itemIds,
            spawned,
            GameEventLogManager.CalculateDropRecoveryTotal(itemIds),
            isBot: false);
        BroadcastGroundItemsSpawned(CurrentArea, spawned);
    }

    internal void DropBotInventoryAtCurrentPosition(long botPlayerId)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null || bot.CurrentArea == AreaType.None) return;

        var removed = _inGameInventoryManager.TakeAllItems(CurrentMapSubId, botPlayerId);
        if (removed.Count == 0) return;

        var emptyBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, botPlayerId);
        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            CurrentMapSubId, botPlayerId, emptyBoard.GetAllItems(), 0, bot.CurrentArea.ToString(), "elimination_drop",
            isBot: true);
        var itemIds = removed
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        if (itemIds.Count == 0) return;

        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, bot.CurrentArea,
            bot.Position.X, bot.Position.Y, itemIds, layout: GroundItemSpawnLayout.EliminationScatter);
        _gameEventLogManager.LogEliminationDrop(
            CurrentMapSubId,
            botPlayerId,
            bot.CurrentArea.ToString(),
            itemIds,
            spawned,
            GameEventLogManager.CalculateDropRecoveryTotal(itemIds),
            isBot: true);
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

    private void SendSurvivorAreaStockStateSnapshot()
    {
        if (CurrentMapSubId <= 0) return;

        var message = BuildSurvivorAreaStockStateMessage();
        using var packet = Packet.Create((int)Protocol.G_TO_C_SURVIVOR_AREA_STOCK_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        Send(packet);
    }

    private void BroadcastSurvivorAreaStockState()
    {
        if (CurrentMapSubId <= 0) return;

        var message = BuildSurvivorAreaStockStateMessage();
        using var packet = Packet.Create((int)Protocol.G_TO_C_SURVIVOR_AREA_STOCK_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        foreach (var session in _getSessionsByInstance(CurrentMapId, CurrentMapSubId))
            session.Send(packet);
    }

    private G_TO_C_SURVIVOR_AREA_STOCK_STATE BuildSurvivorAreaStockStateMessage()
    {
        return new G_TO_C_SURVIVOR_AREA_STOCK_STATE
        {
            Areas = _areaItemStockManager.GetPublicDepletionSnapshot(CurrentMapSubId)
                .Select(state => new SurvivorAreaNaturalStockState
                {
                    AreaType = state.AreaType,
                    IsDepleted = state.IsDepleted,
                    AvailableOrbColors = state.AvailableOrbColors
                })
                .ToList()
        };
    }

    private void SpawnGroundItemsFromExplore(InteractableInfoData info, RngCollectOutcome outcome)
    {
        if (outcome.DroppedItemIds.Count == 0) return;

        var origin = CellToWorldPosition(new Cell(info.CellX, info.CellY));
        var area = (AreaType)info.ZoneId;
        var spawned = _groundItemManager.SpawnItems(CurrentMapSubId, area,
            origin.X, origin.Y, outcome.DroppedItemIds,
            mapId: CurrentMapId,
            discovererPlayerId: PlayerId.GetValueOrDefault(),
            discovererPickupWindow: GroundItemManager.DiscovererPickupWindow);
        BroadcastGroundItemsSpawned(area, spawned);
        long priorityExpiresAtUnixMs = DateTimeOffset.UtcNow
            .Add(GroundItemManager.DiscovererPickupWindow)
            .ToUnixTimeMilliseconds();
        foreach (var item in spawned)
            _gameEventLogManager.LogGroundItemSpawned(
                CurrentMapSubId,
                PlayerId.GetValueOrDefault(),
                item.GroundItemUid,
                item.ItemId,
                area.ToString(),
                priorityExpiresAtUnixMs,
                isBot: false);
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
