using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{

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
