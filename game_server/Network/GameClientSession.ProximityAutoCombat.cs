using game_server.services;
using network.common;
using network.common.data;

namespace game_server.network;

public partial class GameClientSession
{
    internal const int ProximityAutoAttackDealtEventType = 17;
    internal const int ProximityAutoAttackTakenEventType = 18;
    internal const int SurvivorOrbRecoveryEventType = 19;
    internal const int SurvivorWaveSlowEventType = 20;
    internal const int SurvivorSunResonanceEventType = 21;
    internal const int SurvivorWindResonanceEventType = 22;
    internal const int SurvivorWaveResonanceEventType = 23;

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
    internal void ApplyEmotionAfterimageMonsterHit(int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || damage <= 0)
            return;

        // Monster damage has no survivor source, so final PvP damage accounting remains correct.
        ModifyStats(corruptionDelta: damage);
    }

    internal void SendSurvivorOrbResonanceFeedback(SurvivorOrbColor color)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        int eventType = color switch
        {
            SurvivorOrbColor.Red => SurvivorSunResonanceEventType,
            SurvivorOrbColor.Green => SurvivorWindResonanceEventType,
            SurvivorOrbColor.Blue => SurvivorWaveResonanceEventType,
            _ => 0
        };
        if (eventType == 0)
            return;

        SendEncounterEvent(PlayerId.Value, CurrentArea, eventType, 0, 0, 0);
    }

    internal void SendSurvivorWaveSlowFeedback(long sourcePlayerId, int durationMilliseconds)
    {
        if (!PlayerId.HasValue || IsEliminated || durationMilliseconds <= 0)
            return;

        _ = sourcePlayerId;
        SendEncounterEvent(
            PlayerId.Value,
            CurrentArea,
            SurvivorWaveSlowEventType,
            durationMilliseconds,
            0,
            0);
    }

    internal void SendSurvivorOrbRecoveryFeedback(int itemId, int recoveryAmount)
    {
        if (!PlayerId.HasValue || IsEliminated || itemId <= 0 || recoveryAmount <= 0)
            return;

        // Reuse the encounter feedback envelope used by proximity attacks.
        // RevealDelayMs carries the orb item id and DamageValue carries the effective recovery.
        SendEncounterEvent(
            PlayerId.Value,
            CurrentArea,
            SurvivorOrbRecoveryEventType,
            0,
            itemId,
            recoveryAmount);
    }
}
