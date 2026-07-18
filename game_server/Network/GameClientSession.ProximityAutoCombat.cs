using game_server.services;
using network.common;

namespace game_server.network;

public partial class GameClientSession
{
    internal const int ProximityAutoAttackDealtEventType = 17;

    internal void ApplyProximityAutoCombatHit(long sourcePlayerId, AreaType area, int weaponItemId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        if (damage <= 0)
            return;

        ModifyStats(corruptionDelta: damage);

        // For this event type RevealDelayMs is used as lightweight weapon metadata.
        // It keeps the P0 on the existing encounter packet and avoids adding a new input surface.
        SendEncounterEvent(
            sourcePlayerId,
            area,
            EncounterRevealManager.RoomEncounterChalkHitEventType,
            EncounterRevealManager.PairCooldownSeconds,
            weaponItemId);
    }

    internal void SendProximityAutoCombatAttackFeedback(
        long targetPlayerId,
        AreaType area,
        int weaponItemId)
    {
        SendEncounterEvent(
            targetPlayerId,
            area,
            ProximityAutoAttackDealtEventType,
            0,
            weaponItemId);
    }
}
