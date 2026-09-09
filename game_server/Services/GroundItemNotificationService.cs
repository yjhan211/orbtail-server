using game_server.matches;
using game_server.sessions;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>
/// 바닥 아이템 목록을 본인에게 보내거나 새로 생성된 아이템을 같은 구역의 플레이어에게 알린다.
/// 아이템 상태는 변경하지 않으며, 상태 변경과 함께 알릴 때는 호출자가 매치 잠금을 유지한다.
/// </summary>
internal static class GroundItemNotificationService
{
    public static void SendSnapshot(GameClientSession session, AreaType area)
    {
        if (session.MatchingId <= 0 || area == AreaType.None)
        {
            return;
        }
        var items = session.Match.GroundItems.GetSnapshot(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, items);
        session.TrySend(packet);
    }

    public static void BroadcastSpawned(MatchRuntime match, AreaType area, IReadOnlyList<GroundItemInfo> spawned)
    {
        if (spawned.Count == 0)
        {
            return;
        }
        var sessions = new List<GameClientSession>();
        foreach (var session in match.Sessions.Values.ToList())
        {
            if (!session.IsEliminated && session.CurrentArea == area)
                sessions.Add(session);
        }
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, spawned.ToList());
        foreach (var session in sessions)
        {
            session.TrySend(packet);
        }
    }
}
