using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>
///     봇의 탐색 시작·종료를 같은 매치와 구역의 플레이어에게 알린다.
///     별도 상태를 보관하지 않으며, 호출자가 전달한 세션 목록으로 패킷을 보낸다.
/// </summary>
internal static class BotExploreNotifier
{
    public static void NotifyStarted(long matchingId, long botId, int interactId, AreaType area,
        IReadOnlyList<GameClientSession> sessions)
    {
        var message = new G_TO_C_EXPLORE_START { PlayerId = botId, InteractId = interactId };
        Send(matchingId, area, Protocol.G_TO_C_EXPLORE_START, MessagePackSerializer.Serialize(message), sessions);
    }

    public static void NotifyEnded(long matchingId, long botId, AreaType area,
        IReadOnlyList<GameClientSession> sessions)
    {
        var message = new G_TO_C_EXPLORE_END { PlayerId = botId };
        Send(matchingId, area, Protocol.G_TO_C_EXPLORE_END, MessagePackSerializer.Serialize(message), sessions);
    }

    private static void Send(long matchingId, AreaType area, Protocol protocol, byte[] body,
        IReadOnlyList<GameClientSession> sessions)
    {
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue || session.MatchingId != matchingId || session.CurrentArea != area)
                continue;

            using var packet = Packet.Create((int)protocol, session.PlayerId.Value);
            packet.SetBody(body);
            session.TrySend(packet);
        }
    }
}
