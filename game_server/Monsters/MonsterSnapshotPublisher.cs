using game_server.matches.entry;
using game_server.matches;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.monsters;

/// <summary>
///     몬스터 위치 스냅샷을 구역별 패킷으로 나누어 전송한다.
///     시작 전에는 모든 구역을 보내고, 시작 후에는 수신자의 현재 구역만 보낸다.
///     전송 주기는 매치별 SwarmMonsterDirector가 소유하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal static class MonsterSnapshotPublisher
{
    private static readonly TimeSpan MonsterPositionBroadcastInterval = TimeSpan.FromMilliseconds(100);

    internal static bool TryConsumeBroadcastSlot(MatchRuntime match, DateTime nowUtc)
    {
        var monsters = match.Monsters;
        if (nowUtc < monsters.NextMonsterPositionBroadcastAtUtc)
            return false;

        monsters.NextMonsterPositionBroadcastAtUtc = nowUtc + MonsterPositionBroadcastInterval;
        return true;
    }

    /// <summary>
    ///     구역별 전송 (#229 4단계-보정). 청크는 원래부터 구역으로 나뉘어 있었는데 전부를
    ///     전원에게 보내고 있었다. 밀도를 올리면 여기가 먼저 터진다 — 구역당 60마리 × 12구역이면
    ///     한 틱에 720상태를 10명 전원에게 미는 셈이다.
    ///     몹은 같은 구역만 추격하고 클라도 자기 구역만 그리므로 내 구역 것만 보낸다.
    ///     이게 "서버 시뮬 개체와 클라 동기화 개체 분리"의 실체다 — 시뮬은 전역, 동기화는 구역.
    /// </summary>
    internal static void Broadcast(
        long matchingId,
        IReadOnlyCollection<GameClientSession> sessions, IEnumerable<MonsterRuntimeInfo> states)
    {
        // 인트로 구간은 구역 필터를 걷는다 (2026-08-16 유저 결정): 카메라가 운동장에서 열리고
        // 플레이어의 방까지 훑는데, 내 구역 것만 보내면 클라는 그릴 데이터를 아예 못 받는다 —
        // 발원지에서 나가는 몹이 안 보이던 원인이 여기였다. 카운트다운 5초 동안만이다.
        bool preMatch = !MatchStartGate.IsGameplayActive(matchingId);
        foreach (var areaSnapshot in MonsterSnapshotBatcher.GroupByArea(states))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = areaSnapshot.Monsters
            }));

            foreach (var session in sessions)
            {
                if (!preMatch && session.CurrentArea != areaSnapshot.Area)
                    continue;
                session.TrySend(packet);
            }
        }
    }

}
