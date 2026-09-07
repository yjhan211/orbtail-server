using System.Collections.Immutable;
using game_server.sessions;
using network.packets;

namespace game_server.network;

/// <summary>미리 캡처한 세션 목록의 순번으로 수신자를 찾아 같은 순서로 패킷을 보낸다.</summary>
internal static class SessionSnapshotDelivery
{
    public static void SendToCapturedRecipients(
        Packet packet,
        ImmutableArray<int> recipientOrdinals,
        IReadOnlyList<GameClientSession> sessionSnapshot)
    {
        foreach (int ordinal in recipientOrdinals)
        {
            if (TryGetCapturedValue(sessionSnapshot, ordinal, out GameClientSession session))
                session.TrySend(packet);
        }
    }

    internal static bool TryGetCapturedValue<T>(
        IReadOnlyList<T> values,
        int ordinal,
        out T value)
    {
        if ((uint)ordinal < (uint)values.Count)
        {
            value = values[ordinal];
            return true;
        }

        value = default!;
        return false;
    }
}
