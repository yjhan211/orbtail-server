using System;
using System.Threading.Tasks;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    // Used only for resolving already-pending discovery transitions after the target finishes exploring.

    private void SendEncounterEvent(long targetPlayerId, AreaType area, int eventType, int cooldownSeconds,
        int revealDelayMs = 0, int damageValue = 0)
    {
        if (!PlayerId.HasValue || targetPlayerId == 0) return;

        int targetCorruption = -1;
        if (eventType == ProximityAutoAttackDealtEventType || eventType == ProximityAutoAttackTakenEventType)
        {
            var targetSession = _getSessionsByInstance(CurrentMapId, MatchingId)
                .FirstOrDefault(session => session.PlayerId == targetPlayerId);
            if (targetSession != null)
                targetCorruption = targetSession.Corruption;
            else
                targetCorruption = _botPlayerManager.GetBot(MatchingId, targetPlayerId)?.Corruption ?? -1;
        }

        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            targetPlayerId,
            area,
            eventType,
            cooldownSeconds,
            revealDelayMs,
            damageValue,
            targetCorruption);
        Send(packet);
    }

}
