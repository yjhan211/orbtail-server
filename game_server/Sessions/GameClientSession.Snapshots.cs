using game_server.services;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

// 초기 입장 시 플레이어·봇·구역 폐쇄 상태를 패킷으로 전송한다.
public partial class GameClientSession
{
    private Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue || IsEliminated)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal || !PlayerId.HasValue || IsEliminated)
            {
                return Task.CompletedTask;
            }

            var sessions = new List<GameClientSession>();
            foreach (var session in match.Sessions.Snapshot())
            {
                if (!session.PlayerId.HasValue || session.PlayerId == PlayerId)
                {
                    continue;
                }
                if (session.IsEliminated || session.CurrentArea != CurrentArea)
                {
                    continue;
                }
                sessions.Add(session);
            }

            if (sessions.Count > 0)
            {
                using var others = PacketMaker.G_TO_C_OBJECT_INFO(sessions.Select(s => s.CaptureGameObjectInfo()).ToList());
                TrySend(others);
            }

            using (var mine = PacketMaker.G_TO_C_OBJECT_INFO([CaptureGameObjectInfo()]))
            {
                foreach (var session in sessions)
                {
                    session.TrySend(mine);
                }
            }

            var bots = match.Bots.GetBots(MatchingId).Where(bot => !bot.IsEliminated && bot.CurrentArea == CurrentArea).ToList();
            var objects = bots.Select(bot => match.Bots.SynthesizeGameObjectInfo(MatchingId, bot.PlayerId)).OfType<GameObjectInfo>().ToList();
            if (objects.Count <= 0)
            {
                return Task.CompletedTask;
            }

            using var packet = PacketMaker.G_TO_C_OBJECT_INFO(objects);
            TrySend(packet);
        }

        return Task.CompletedTask;
    }

    /// <summary>이동 정보와 현재 행동 상태를 복사한다. 호출자는 매치 잠금을 보유해야 한다.</summary>
    internal GameObjectInfo CaptureGameObjectInfo() =>
        _playerMovement.CaptureGameObjectInfo(_condition.State);

    /// <summary>입장한 클라이언트에 자기장 수축 시작 시각을 보낸다. 경계는 공용 규칙으로 계산한다.</summary>
    private void SendPressureFieldState()
    {
        if (MatchingId <= 0 || !Config.SWARM_PRESSURE_FIELD_ENABLED) return;
        var state = Match.Closures.GetMatchingState();
        if (state == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
        {
            StartedAtUnixMs = new DateTimeOffset(state.GameStartTime).ToUnixTimeMilliseconds()
        }));
        TrySend(packet);
    }
}
