using System.Linq;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    /// <summary>
    ///     타겟 구역 위치 전송 — 미니맵 타겟 마커의 단일 공급원 (주기 타이머가 호출).
    /// </summary>
    public void SendTargetLocation()
    {
        if (!PlayerId.HasValue || IsEliminated || IsGameEnded || TargetPlayerId == 0) return;
        // 1인 매칭으로 본인이 본인을 타겟으로 가지는 케이스 방어
        if (TargetPlayerId == PlayerId.Value) return;

        AreaType targetArea;
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var targetSession = allSessions.FirstOrDefault(s => s.PlayerId == TargetPlayerId);
        if (targetSession != null)
        {
            if (targetSession.IsEliminated) return;
            targetArea = targetSession.CurrentArea;
        }
        else
        {
            // #26: 타겟이 봇인 경우 BotPlayerManager에서 위치 조회
            var bot = _botPlayerManager.GetBot(CurrentMapSubId, TargetPlayerId);
            if (bot == null || bot.IsEliminated || bot.PlayerMatchStatus == PlayerMatchStatus.SPECTATING) return;
            targetArea = bot.CurrentArea;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_TARGET_LOCATION, PlayerId.Value);
        var msg = new G_TO_C_TARGET_LOCATION
        {
            TargetPlayerId = TargetPlayerId,
            AreaType = targetArea
        };
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
        Logger.LogDebug("Target location sent: MatchingId={MatchingId}, PlayerId={PlayerId}, Target={TargetPlayerId}, Area={Area}",
            CurrentMapSubId, PlayerId, TargetPlayerId, targetArea);
    }
}
