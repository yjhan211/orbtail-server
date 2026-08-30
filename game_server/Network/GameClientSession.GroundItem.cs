using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private Task HandleGroundItemPickup(C_TO_G_GROUND_ITEM_PICKUP msg)
    {
        if (!PlayerId.HasValue)
        {
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        Task result = Task.CompletedTask;
        bool executed = _executeMatchRuntime(
            CurrentMapSubId,
            () => result = HandleGroundItemPickupCore(msg));
        if (!executed)
            SendGroundItemPickupResult(msg.GroundItemUid, 0, false, false, ErrorCode.INVALID_GAME_STATE);
        return result;
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
                    CurrentMapSubId,
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

                bool added = _inGameInventoryManager.TryAddItemWithCapacity(
                    CurrentMapSubId, PlayerId.Value, item.ItemId, Config.GetOrbCapacity(),
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
            if (attemptedItem?.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
            {
                Logger.LogDebug(
                    "Summon stone pickup rejected: MatchingId={MatchingId}, PlayerId={PlayerId}, GroundItemUid={GroundItemUid}, Status={Status}, PlayerArea={PlayerArea}, ItemArea={ItemArea}, Player=({PlayerX:F2},{PlayerY:F2}), Item=({ItemX:F2},{ItemY:F2})",
                    CurrentMapSubId,
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
                    CurrentMapSubId, PlayerId.Value, attemptedItem.ItemId,
                    deniedStaminaRecovery + deniedCorruptionRecovery, 0,
                    $"denied_{status.ToString().ToLowerInvariant()}", isBot: false);
            }
            if (attemptedItem != null && error == ErrorCode.INVENTORY_FULL)
            {
                var board = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                _gameEventLogManager.LogOrbPickupBlockedFull(
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
            var summonState = _summonStoneManager.AddStones(CurrentMapSubId, PlayerId.Value, 1);
            SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
            _gameEventLogManager.LogSummonStoneAward(
                CurrentMapSubId,
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
                SwarmHeartPickupCallback?.Invoke(CurrentMapSubId, PlayerId.Value);
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
        if (!summonStonePickup && !jamPickup && !bootsPickup && !keyPickup)
        {
            var boardAfterPickup = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            _gameEventLogManager.LogOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, boardAfterPickup.GetAllItems(),
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
        Send(packet);
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
        Send(packet);
    }

    internal void ResetJam(bool notify = false)
    {
        JamCount = 0;
        if (!notify || !PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_JAM_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_JAM_STATE { JamCount = 0 }));
        Send(packet);
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
            _inGameInventoryManager,
            _groundItemManager,
            CurrentMapSubId,
            PlayerId.Value,
            CurrentArea,
            position.X,
            position.Y,
            CurrentMapId);
        if (drop.RemovedItems.Count == 0) return;

        var emptyBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        _gameEventLogManager.LogOrbBoardTransition(
            CurrentMapSubId, PlayerId.Value, emptyBoard.GetAllItems(), 0, CurrentArea.ToString(), "elimination_drop",
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
            CurrentMapSubId,
            PlayerId.Value,
            CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: false);
        BroadcastGroundItemsSpawned(CurrentArea, drop.SpawnedItems);
    }

    internal void DropBotInventoryAtCurrentPosition(long botPlayerId)
    {
        var bot = _botPlayerManager.GetBot(CurrentMapSubId, botPlayerId);
        if (bot == null || bot.CurrentArea == AreaType.None)
            return;

        var drop = EliminationInventoryDropper.DropAll(
            _inGameInventoryManager,
            _groundItemManager,
            CurrentMapSubId,
            botPlayerId,
            bot.CurrentArea,
            bot.Position.X,
            bot.Position.Y,
            _botPlayerManager.GetMatchingMapId(CurrentMapSubId));
        if (drop.RemovedItems.Count == 0)
            return;

        var emptyBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, botPlayerId);
        _gameEventLogManager.LogOrbBoardTransition(
            CurrentMapSubId, botPlayerId, emptyBoard.GetAllItems(), 0, bot.CurrentArea.ToString(), "elimination_drop",
            isBot: true);

        if (drop.DroppedItemIds.Count == 0)
            return;

        _gameEventLogManager.LogEliminationDrop(
            CurrentMapSubId,
            botPlayerId,
            bot.CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: true);
        BroadcastGroundItemsSpawned(bot.CurrentArea, drop.SpawnedItems);
    }

    // 스냅샷 청크 크기: 패킷 버퍼(2048) 안에 안전히 들어가는 마릿수.
    // 20개도 초과했다(실측 2523바이트 — 개당 ~125바이트) — 10개면 여유 포함 절반 이하.
    private const int GroundItemSnapshotChunkSize = 10;

    private void SendGroundItemSnapshot(AreaType area)
    {
        if (CurrentMapSubId <= 0 || area == AreaType.None) return;
        var items = _groundItemManager.GetSnapshot(CurrentMapSubId, area);
        int remaining = _areaItemStockManager.GetRemainingCount(CurrentMapSubId, (int)area);
        // 버퍼 초과 방지 (#226): 웨이브 모드로 바닥 아이템이 수백 개까지 쌓여 단일 패킷이
        // 2048을 넘었다(실측 9963). 첫 청크는 SNAPSHOT(클라: 구역 교체), 이후 청크는
        // SPAWN(클라: 누적) — 기존 수신 의미를 그대로 이용해 프로토콜 변경 없이 나눈다.
        for (int index = 0; index < items.Count || index == 0; index += GroundItemSnapshotChunkSize)
        {
            var chunk = items.Skip(index).Take(GroundItemSnapshotChunkSize).ToList();
            using var packet = index == 0
                ? PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, remaining, chunk)
                : PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, chunk);
            Send(packet);
        }
    }

    private void SendAreaStockStateSnapshot()
    {
        if (CurrentMapSubId <= 0) return;

        var message = BuildAreaStockStateMessage();
        using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_STOCK_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        Send(packet);
    }

    private G_TO_C_AREA_STOCK_STATE BuildAreaStockStateMessage()
    {
        return new G_TO_C_AREA_STOCK_STATE
        {
            Areas = _areaItemStockManager.GetPublicDepletionSnapshot(CurrentMapSubId)
                .Select(state => new AreaNaturalStockState
                {
                    AreaType = state.AreaType,
                    IsDepleted = state.IsDepleted,
                    AvailableOrbColors = state.AvailableOrbColors
                })
                .ToList()
        };
    }

    private void BroadcastGroundItemsSpawned(AreaType area, IReadOnlyList<GroundItemInfo> spawned)
    {
        if (spawned.Count == 0) return;
        int remaining = _areaItemStockManager.GetRemainingCount(CurrentMapSubId, (int)area);
        var sessions = GetSessionsInArea(
            _getSessionsByInstance(CurrentMapId, CurrentMapSubId), area, excludeSelf: false);
        // 청크로 나눠 보낸다 (#229): 드롭 개수가 열려 있어 단일 패킷이 버퍼 2048을 넘길 수 있다.
        // 넘기면 예외가 호출부까지 올라가 드롭 처리 전체가 죽는다 — 봇 탈락에서 실제로 났다.
        for (int offset = 0; offset < spawned.Count; offset += GroundItemSpawnBroadcastChunkSize)
        {
            var chunk = spawned.Skip(offset).Take(GroundItemSpawnBroadcastChunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, chunk);
            foreach (var session in sessions) session.Send(packet);
        }
    }

    private const int GroundItemSpawnBroadcastChunkSize = 8;

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
