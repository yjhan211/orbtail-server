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
    internal const int OrbRecoveryEventType = 19;
    internal const int SwarmAfterimageMonsterAttackTakenEventType = 24;
    internal const int SwarmAfterimageMonsterAttackDealtEventType = 25;
    internal const int SwarmAttackEventDealtEventType = 26;
    internal const int SwarmAttackEventTakenEventType = 27;
    // 파도 침수 감속 (#268): 20 — 클라 MapManager.PlayerVisibility의 EncounterEventWaveSlow와 짝.
    internal const int WaveSlowEventType = 20;

    /// <summary>
    ///     침수 통지 (#268, 2026-08-25): 피해자 클라가 이동 감속(RevealDelayMs = 지속 ms)을
    ///     걸고 디버프 창에 침수를 띄운다. 변위(당김·밀침) 실험은 기각 — 감속만 남는다.
    /// </summary>
    public void SendSwarmWaveSlow(long ownerPlayerId, AreaType area, int durationMs)
    {
        SendEncounterEvent(ownerPlayerId, area, WaveSlowEventType,
            cooldownSeconds: 0,
            revealDelayMs: durationMs);
    }

    // 화상 (#268): 29 — 클라 MapManager.PlayerVisibility의 EncounterEventSunBurn과 짝.
    internal const int SunBurnEventType = 29;

    /// <summary>화상 통지 (#268, 2026-08-25): 디버프 창 표시용 — 틱 피해는 서버가 정산한다.</summary>
    public void SendSwarmSunBurn(long ownerPlayerId, AreaType area, int durationMs)
    {
        SendEncounterEvent(ownerPlayerId, area, SunBurnEventType,
            cooldownSeconds: 0,
            revealDelayMs: durationMs);
    }

    // 상처 (#268): 30 — 클라 MapManager.PlayerVisibility의 EncounterEventWindWound와 짝.
    internal const int WindWoundEventType = 30;

    /// <summary>상처 통지 (#268, 2026-08-25): 디버프 창 표시용 — 치명타 굴림은 서버가 한다.</summary>
    public void SendSwarmWindWound(long ownerPlayerId, AreaType area, int durationMs)
    {
        SendEncounterEvent(ownerPlayerId, area, WindWoundEventType,
            cooldownSeconds: 0,
            revealDelayMs: durationMs);
    }

    // 도트 틱 플래그 (#268 화상, 2026-08-26): 17/18 이벤트의 cooldownSeconds는 빈 자리라
    // 플래그 비트로 빌려 쓴다 — bit0 = 도트 틱(직격과 다른 연출: 작은 주황 숫자, 피격 연출 없음).
    internal const int ProximityHitFlagDotTick = 1;

    internal void ApplyProximityAutoCombatHit(
        long sourcePlayerId, AreaType area, int weaponItemId, int damage, bool dotTick = false)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        if (damage <= 0)
            return;

        _gameEventLogManager.LogHit(
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
            dotTick ? ProximityHitFlagDotTick : 0,
            weaponItemId,
            damage);
    }

    internal void SendProximityAutoCombatAttackFeedback(
        long targetPlayerId,
        AreaType area,
        int weaponItemId,
        int damage,
        bool dotTick = false)
    {
        SendEncounterEvent(
            targetPlayerId,
            area,
            ProximityAutoAttackDealtEventType,
            dotTick ? ProximityHitFlagDotTick : 0,
            weaponItemId,
            damage);
    }

    /// <summary>#227 6단계: 속성별 공격 사건의 합산 피해를 한 번만 적용·표시한다.</summary>
    internal void ApplySwarmAttackEventHit(long sourcePlayerId, AreaType area, int weaponItemId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || damage <= 0)
            return;

        _gameEventLogManager.LogHit(
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

    internal void SendSwarmAfterimageMonsterAttackFeedback(
        int monsterId, AreaType area, int weaponItemId, int damage, bool critical = false,
        bool noProjectile = false)
    {
        if (!PlayerId.HasValue || IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0)
            return;

        // targetCorruption is event-specific metadata here: zero means a Wave splash hit,
        // so the client preserves its damage feedback without replaying the projectile.
        // cooldownSeconds는 이 이벤트에서 안 쓰는 자리라 플래그 비트로 빌려 쓴다 (#229 임시):
        // bit0 = 치명타, bit1 = 투사체 없음(#232 교차사격 쓸기 — 모양이 이미 그 자리를 지나갔다,
        // 숫자만 띄운다).
        int flags = (critical ? 1 : 0) | (noProjectile ? 2 : 0);
        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            PlayerId.Value, area, SwarmAfterimageMonsterAttackDealtEventType, flags,
            weaponItemId, damage, monsterId);
        Send(packet);
    }
    // displayDamage: 오브 HP 모델에서 오염 델타(연출용 1)와 클라 표시 피해량(실제 오브 피해)을 분리한다.
    internal void ApplySwarmAfterimageMonsterHit(int monsterId, int damage, int? displayDamage = null)
    {
        if (!PlayerId.HasValue || IsEliminated || monsterId <= 0 || damage <= 0)
            return;

        int corruptionBefore = Corruption;
        int corruptionAfter = Math.Min(MaxCorruption, corruptionBefore + damage);
        bool isLethal = corruptionBefore < MaxCorruption && corruptionAfter >= MaxCorruption;
        _gameEventLogManager.LogSwarmAfterimageHit(
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
        SendEncounterEvent(PlayerId.Value, CurrentArea, SwarmAfterimageMonsterAttackTakenEventType,
            0, monsterId, displayDamage ?? damage);
    }

    internal void SendOrbRecoveryFeedback(int itemId, int recoveryAmount)
    {
        if (!PlayerId.HasValue || IsEliminated || itemId <= 0 || recoveryAmount <= 0)
            return;

        // Reuse the encounter feedback envelope used by proximity attacks.
        // RevealDelayMs carries the orb item id and DamageValue carries the effective recovery.
        SendEncounterEvent(
            PlayerId.Value,
            CurrentArea,
            OrbRecoveryEventType,
            0,
            itemId,
            recoveryAmount);
    }
}
