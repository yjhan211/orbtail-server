using game_server.services;
using network.common;

namespace game_server.network;

public partial class GameClientSession
{
    internal const int ProximityAutoAttackDealtEventType = 17;
    internal const int ProximityAutoAttackTakenEventType = 18;

    internal void ApplyProximityAutoCombatHit(long sourcePlayerId, AreaType area, int weaponItemId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        if (damage <= 0)
            return;

        _gameEventLogManager.LogSurvivorHit(
            CurrentMapSubId,
            sourcePlayerId,
            PlayerId.Value,
            weaponItemId,
            damage,
            Corruption < MaxCorruption && Corruption + damage >= MaxCorruption,
            BotPlayerManager.IsBotPlayerId(sourcePlayerId),
            DateTimeOffset.UtcNow);

        ModifyStats(corruptionDelta: damage, attackerPlayerId: sourcePlayerId);

        // RevealDelayMs carries weapon metadata; DamageValue preserves the authoritative hit result.
        SendEncounterEvent(
            sourcePlayerId,
            area,
            ProximityAutoAttackTakenEventType,
            0,
            weaponItemId,
            damage);
    }

    internal void SendProximityAutoCombatAttackFeedback(
        long targetPlayerId,
        AreaType area,
        int weaponItemId,
        int damage)
    {
        SendEncounterEvent(
            targetPlayerId,
            area,
            ProximityAutoAttackDealtEventType,
            0,
            weaponItemId,
            damage);
    }
}
