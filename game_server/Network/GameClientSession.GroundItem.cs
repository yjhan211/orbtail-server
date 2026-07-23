using game_server.services;
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
            SendGroundItemPickupResult(msg.GroundItemUid, claimedItem?.ItemId ?? 0, false, false, error);
            return Task.CompletedTask;
        }

        if (autoUsed)
        {
            ModifyStats(staminaDelta: staminaRecovery, corruptionDelta: -corruptionRecovery);
            _gameEventLogManager.LogRecoveryUse(
                CurrentMapSubId, PlayerId.Value, claimedItem.ItemId,
                corruptionRecovery, source: "ground_auto_use", isBot: false);
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
        SendGroundItemPickupResult(claimedItem.GroundItemUid, claimedItem.ItemId, true, autoUsed, ErrorCode.SUCCESS);
        return Task.CompletedTask;
    }

    private Task HandleDropGroundItem(C_TO_G_DROP_GROUND_ITEM msg)
    {
        // P1 보드는 자유 버리기로 정리할 수 없다. 재활용 지역만 별도 권위 규칙으로 추가한다.
        // 구버전 클라이언트나 임의 패킷도 인벤토리·월드 상태를 바꾸지 못하게 서버에서 차단한다.
        SendErrorResponse(ErrorCode.ITEM_NOT_USABLE, "Manual item discard is disabled");
        return Task.CompletedTask;
    }
    internal void DropAllInventoryAtCurrentPosition()
    {
        if (!PlayerId.HasValue || _lastValidatedPosition == null || CurrentArea == AreaType.None) return;

        var removed = _inGameInventoryManager.TakeAllItems(CurrentMapSubId, PlayerId.Value);
        if (removed.Count == 0) return;

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

        _gameEventLogManager.LogEliminationDrop(
            CurrentMapSubId,
            PlayerId.Value,
            CurrentArea.ToString(),
            itemIds,
            GameEventLogManager.CalculateDropRecoveryTotal(itemIds),
            isBot: false);

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

        var itemIds = removed
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        if (itemIds.Count == 0) return;

        _gameEventLogManager.LogEliminationDrop(
            CurrentMapSubId,
            botPlayerId,
            bot.CurrentArea.ToString(),
            itemIds,
            GameEventLogManager.CalculateDropRecoveryTotal(itemIds),
            isBot: true);
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
