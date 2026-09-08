using game_server.services;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    internal void DropAllInventoryAtCurrentPosition()
    {
        if (!PlayerId.HasValue || LastValidatedPosition == null || CurrentArea == AreaType.None) return;

        var position = LastValidatedPosition;
        var drop = EliminationInventoryDropper.DropAll(
            Match.Inventory,
            Match.GroundItems,
            MatchingId,
            PlayerId.Value,
            CurrentArea,
            position.X,
            position.Y,
            CurrentMapId);
        if (drop.RemovedItems.Count == 0) return;

        var emptyBoard = Match.Inventory.GetPlayerInventory(PlayerId.Value);
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
        var items = Match.GroundItems.GetSnapshot(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, items.ToList());
        TrySend(packet);
    }
    private void BroadcastGroundItemsSpawned(AreaType area, IReadOnlyList<GroundItemInfo> spawned)
    {
        if (spawned.Count == 0) return;
        var sessions = Match.Sessions.GetInArea(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, spawned.ToList());
        foreach (var session in sessions) session.TrySend(packet);
    }
}
