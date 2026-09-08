using game_server.sessions;
using network.common;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// 탈락한 플레이어의 인벤토리를 현재 위치에 떨어뜨리고 인벤토리 변경·드롭 로그·주변 통지를 처리한다.
/// 실제 드롭 계산과 상태 변경은 EliminationInventoryDropper에 맡기며, 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class GroundItemDropService(GameEventLogManager eventLogs)
{
    public void DropAll(GameClientSession session)
    {
        if (!session.PlayerId.HasValue || session.LastValidatedPosition == null || session.CurrentArea == AreaType.None) return;

        var position = session.LastValidatedPosition;
        var drop = EliminationInventoryDropper.DropAll(
            session.Match.Inventory,
            session.Match.GroundItems,
            session.MatchingId,
            session.PlayerId.Value,
            session.CurrentArea,
            position.X,
            position.Y,
            session.CurrentMapId);
        if (drop.RemovedItems.Count == 0) return;

        var emptyBoard = session.Match.Inventory.GetPlayerInventory(session.PlayerId.Value);
        eventLogs.LogOrbBoardTransition(
            session.MatchingId, session.PlayerId.Value, emptyBoard.GetAllItems(), 0, session.CurrentArea.ToString(), "elimination_drop",
            isBot: false);
        foreach (var item in drop.RemovedItems)
            session.Notifications.SendInventoryUpdate(new InGameItemInfo
            {
                ItemUid = item.ItemUid,
                ItemId = item.ItemId,
                Count = 0,
                GiftState = item.GiftState
            });

        if (drop.DroppedItemIds.Count == 0) return;

        eventLogs.LogEliminationDrop(
            session.MatchingId,
            session.PlayerId.Value,
            session.CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: false);
        GroundItemNotificationService.BroadcastSpawned(session.Match, session.CurrentArea, drop.SpawnedItems);
    }
}
