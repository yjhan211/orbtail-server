using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.matches.monsters;

internal static class MonsterSnapshotPublisher
{
    private static readonly TimeSpan MonsterPositionBroadcastInterval = TimeSpan.FromMilliseconds(100);

    internal static bool TryConsumeBroadcastSlot(MatchRuntime match, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Monster snapshot broadcast requires the match lock.");
        }
        var monsters = match.Monsters;
        if (nowUtc < monsters.NextMonsterPositionBroadcastAtUtc)
        {
            return false;
        }

        monsters.NextMonsterPositionBroadcastAtUtc = nowUtc + MonsterPositionBroadcastInterval;
        return true;
    }

    internal static void Broadcast(MatchRuntime match, IReadOnlyCollection<GameClientSession> sessions, IEnumerable<MonsterRuntimeInfo> states)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Monster snapshot broadcast requires the match lock.");
        }
        bool preMatch = !match.IsGameplayActive();
        foreach (var areaSnapshot in MonsterSnapshotBatcher.GroupByArea(states))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = areaSnapshot.Monsters
            }));

            foreach (var session in sessions)
            {
                if (!preMatch && session.Player.CurrentArea != areaSnapshot.Area)
                {
                    continue;
                }
                session.TrySend(packet);
            }
        }
    }
}
