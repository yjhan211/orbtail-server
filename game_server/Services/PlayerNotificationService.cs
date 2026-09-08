using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>한 플레이어의 인벤토리·체력·행동 상태를 전송한다. 상태 변경과 함께 호출할 때는 매치 잠금을 유지한다.</summary>
internal sealed class PlayerNotificationService(GameClientSession session, ILogger logger)
{
    public void SendInventoryList()
    {
        if (!session.PlayerId.HasValue) return;

        var inventory = session.Match.Inventory.GetPlayerInventory(session.PlayerId.Value);
        var items = inventory.GetAllItems();
        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_LIST(items);
        session.TrySend(packet);



        logger.LogDebug("Sent InGameInventory list to PlayerId={PlayerId}, ItemCount={Count}", session.PlayerId, items.Count);
    }

    public void SendInventoryUpdate(InGameItemInfo item)
    {
        if (!session.PlayerId.HasValue) return;

        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([item]);
        session.TrySend(packet);

        logger.LogDebug(
            "Sent InGameInventory update to PlayerId={PlayerId}, ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
            session.PlayerId, item.ItemUid, item.ItemId, item.Count);
    }

    public void SendStats(PlayerCondition.HealthChange change)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(change.After, change.RequestedDelta);
        session.TrySend(packet);
    }

    /// <summary>확정된 행동 상태를 같은 구역에 전송한다. 상태 자체는 변경하지 않는다.</summary>
    public void SendState(bool includeSelf = true)
    {
        if (!session.PlayerId.HasValue) return;
        var recipients = session.Match.Sessions.GetInArea(
            session.CurrentArea, includeSelf ? null : session.PlayerId);
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(session.PlayerId.Value, session.Condition.State);
        foreach (var recipient in recipients)
            recipient.TrySend(packet);
        logger.LogInformation("Player state sent: PlayerId={PlayerId}, State={State}, Recipients={Count}",
            session.PlayerId, session.Condition.State, recipients.Count);
    }
}
