using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.packets;

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
    internal const int EmotionAfterimageMonsterAttackTakenEventType = 24;
    internal const int EmotionAfterimageMonsterAttackDealtEventType = 25;
    internal const int SwarmAttackEventDealtEventType = 26;
    internal const int SwarmAttackEventTakenEventType = 27;

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

    /// <summary>#227 6단계: 속성별 공격 사건의 합산 피해를 한 번만 적용·표시한다.</summary>
    internal void ApplySwarmAttackEventHit(long sourcePlayerId, AreaType area, int weaponItemId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || damage <= 0)
            return;

        _gameEventLogManager.LogSurvivorHit(
            CurrentMapSubId,
            sourcePlayerId,
            PlayerId.Value,
            weaponItemId,
            damage,
            Corruption < MaxCorruption && Corruption + damage >= MaxCorruption,
            BotPlayerManager.IsBotPlayerId(sourcePlayerId),
            DateTimeOffset.UtcNow,
            damageSourceType: "swarm_attack_event");
        ModifyStats(corruptionDelta: damage, attackerPlayerId: sourcePlayerId);
        SendEncounterEvent(
            sourcePlayerId,
            area,
            SwarmAttackEventTakenEventType,
            0,
            weaponItemId,
            damage);
    }

    internal void SendSwarmAttackEventFeedback(
        long targetPlayerId,
        AreaType area,
        int weaponItemId,
        int damage)
    {
        SendEncounterEvent(
            targetPlayerId,
            area,
            SwarmAttackEventDealtEventType,
            0,
            weaponItemId,
            damage);
    }

    internal void SendEmotionAfterimageMonsterAttackFeedback(int monsterId, AreaType area, int weaponItemId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0)
            return;

        // targetCorruption is event-specific metadata here: zero means a Wave splash hit,
        // so the client preserves its damage feedback without replaying the projectile.
        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            PlayerId.Value, area, EmotionAfterimageMonsterAttackDealtEventType, 0,
            weaponItemId, damage, monsterId);
        Send(packet);
    }
    // displayDamage: 오브 HP 모델에서 오염 델타(연출용 1)와 클라 표시 피해량(실제 오브 피해)을 분리한다.
    internal void ApplyEmotionAfterimageMonsterHit(int monsterId, int damage, int? displayDamage = null)
    {
        if (!PlayerId.HasValue || IsEliminated || monsterId <= 0 || damage <= 0)
            return;

        int corruptionBefore = Corruption;
        int corruptionAfter = Math.Min(MaxCorruption, corruptionBefore + damage);
        bool isLethal = corruptionBefore < MaxCorruption && corruptionAfter >= MaxCorruption;
        _gameEventLogManager.LogEmotionAfterimageHit(
            CurrentMapSubId,
            monsterId,
            PlayerId.Value,
            CurrentArea.ToString(),
            damage,
            corruptionBefore,
            corruptionAfter,
            isLethal,
            isBot: false,
            DateTimeOffset.UtcNow);
        Logger.LogInformation(
            "Emotion afterimage attack: MatchingId={MatchingId}, MonsterId={MonsterId}, Target={Target}, TargetKind=Human, Damage={Damage}, CorruptionBefore={CorruptionBefore}, CorruptionAfter={CorruptionAfter}, Killed={Killed}",
            CurrentMapSubId,
            monsterId,
            PlayerId.Value,
            damage,
            corruptionBefore,
            corruptionAfter,
            isLethal);
        // Monster damage has no survivor source, so final PvP damage accounting remains correct.
        ModifyStats(corruptionDelta: damage);
        // The encounter envelope carries the visual source (monster id) and the authoritative damage value.
        SendEncounterEvent(PlayerId.Value, CurrentArea, EmotionAfterimageMonsterAttackTakenEventType,
            0, monsterId, displayDamage ?? damage);
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
