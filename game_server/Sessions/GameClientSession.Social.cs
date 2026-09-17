using network.common.data;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     플레이어의 소셜 액션 요청을 받아 같은 구역의 플레이어들(본인 포함)에게 전달한다.
/// </summary>
public partial class GameClientSession
{
    private Task HandleSocialAction(C_TO_G_SOCIAL_ACTION msg)
    {
        if (!PlayerId.HasValue)
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
            if (match.IsEnded || IsGameplayActionBlocked(out _))
            {
                return Task.CompletedTask;
            }

            var sameAreaSessions = new List<GameClientSession>();
            foreach (var session in match.GetSessions())
            {
                if (!session.Player.IsEliminated && GameMapData.GetCurrentArea(session.Player.GameInfo.ObjectInfo.MapId, session.Player.GameInfo.ObjectInfo.Cell) == GameMapData.GetCurrentArea(Player.GameInfo.ObjectInfo.MapId, Player.GameInfo.ObjectInfo.Cell))
                    sameAreaSessions.Add(session);
            }
            var broadcast = new G_TO_C_SOCIAL_ACTION
            {
                PlayerId = PlayerId.Value,
                SocialActionType = msg.SocialActionType
            };
            byte[] bodyBytes = MessagePackSerializer.Serialize(broadcast);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_SOCIAL_ACTION, session.PlayerId ?? 0);
                packet.SetBody(bodyBytes);
                session.TrySend(packet);
            }
        }
        return Task.CompletedTask;
    }
}
