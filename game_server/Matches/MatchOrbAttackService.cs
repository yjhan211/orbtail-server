using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치 틱마다 플레이어의 오브 발동과 생성된 공격의 처리를 조율한다.
///     태양 투사체의 피격,화상 / 파도 기폭,감속 / 바람의 즉시 피해,상처를 처리한다.
/// </summary>
internal sealed class MatchOrbAttackService(
    MatchCombatDamageService combatDamage,
    PlayerOrbService playerOrbs,
    MatchSynchronizationService synchronization)
{
    /// <summary>
    ///     이미 존재하는 공격을 먼저 정산하고
    ///     살아남은 플레이어만 오브를 발동한 뒤 바람 칼날을 즉시 적용한다.
    /// </summary>
    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb attack tick requires the match lock.");
        }

        ProcessWaveAttacks(runtime, nowUtc);
        ProcessSunAttacks(runtime, nowUtc);
        ProcessSunBurns(runtime, nowUtc);
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateOrbs(runtime, player, nowUtc);
        }
        ProcessWindAttacks(runtime, nowUtc);
    }

    internal void ProcessWaveAttacks(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Wave orb attacks require the match lock.");
        }

        if (runtime.IsEnded)
        {
            return;
        }

        for (int index = runtime.PendingWaveAttacks.Count - 1; index >= 0; index--)
        {
            var attack = runtime.PendingWaveAttacks[index];
            if (nowUtc < attack.ExplodeAtUtc)
            {
                continue;
            }
            runtime.PendingWaveAttacks.RemoveAt(index);

            var (monsters, players) = MatchOrbTarget.CollectTargetsInRadius(runtime, attack.OwnerId, attack.Area, attack.Position, attack.Radius);
            foreach (var monster in monsters)
            {
                combatDamage.ApplyMonsterHit(runtime, monster.MonsterId, attack.OwnerId, attack.SourceItemId, attack.Area, attack.Damage, nowUtc);
            }

            foreach (var participant in players)
            {
                combatDamage.ApplyOrbShock(runtime, attack.OwnerId, attack.SourceItemId, attack.Area, participant, nowUtc, Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);
                if (runtime.IsEnded)
                {
                    return;
                }
                if (participant.IsEliminated || !attack.AppliesSlow)
                {
                    continue;
                }
                participant.StatusEffects.Apply(PlayerStatusEffectKind.WaveSlow, nowUtc.AddSeconds(Config.SWARM_WAVE_SLOW_SECONDS));
                synchronization.QueueStatusEffect(runtime, participant, attack.OwnerId, attack.Area, CombatStatusEffectKind.WaveOrbSlow, Config.SWARM_WAVE_SLOW_SECONDS);
            }
        }
    }

    /// <summary>
    ///     예고가 끝난 태양 선의 앞머리를 전진시키고 이번 틱에 지나간 구간의 몸통을 맞춘다.
    ///     한 선은 같은 대상을 한 번만 맞추며, 끝까지 쓴 선은 제거한다.
    /// </summary>
    internal void ProcessSunAttacks(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        var attacks = runtime.PendingSunAttacks;
        for (int index = attacks.Count - 1; index >= 0; index--)
        {
            var attack = attacks[index];
            if (nowUtc < attack.ArmedAtUtc)
            {
                continue;
            }

            float halfWidth = OrbData.GetSunWidth(attack.WeaponItemId) * 0.5f; // 선 판정 폭의 절반(선의 중심축에서 양옆)
            float sweepStart = -halfWidth; // 앞머리가 출발하는 위치
            float sweepEnd = attack.GroundLength + halfWidth; // 앞머리가 도달해야 하는 마지막 위치
            float elapsedSeconds = (float)(nowUtc - attack.ArmedAtUtc).TotalSeconds;
            float front = sweepStart + elapsedSeconds * Config.SWARM_SUN_SWEEP_SPEED; // 출발점에서 속도*시간만큼 전진한 앞머리
            if (nowUtc >= attack.ExpiresAtUtc || front > sweepEnd)
            {
                front = sweepEnd;
            }

            float lastFront = attack.LastFront ?? sweepStart;
            attack.LastFront = front;

            var (monsters, players) = MatchOrbTarget.CollectTargetsOnSunLine(runtime, attack, halfWidth, lastFront, front);
            foreach (var monster in monsters)
            {
                attack.HitMonsters.Add(monster.MonsterId);
                combatDamage.ApplyMonsterHit(runtime, monster.MonsterId, attack.OwnerId, attack.WeaponItemId, attack.Area, attack.Damage, nowUtc);
            }
            foreach (var participant in players)
            {
                attack.HitVictims.Add(participant.PlayerId);
                combatDamage.ApplyOrbShock(runtime, attack.OwnerId, attack.WeaponItemId, attack.Area, participant, nowUtc);
                if (runtime.IsEnded)
                {
                    return;
                }
                if (participant.IsEliminated)
                {
                    continue;
                }
                participant.StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(attack.OwnerId, attack.WeaponItemId, attack.Area, nowUtc.AddSeconds(Config.SWARM_SUN_BURN_SECONDS), nowUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS)));
                synchronization.QueueStatusEffect(runtime, participant, attack.OwnerId, attack.Area, CombatStatusEffectKind.SunBurn, Config.SWARM_SUN_BURN_SECONDS);
            }
            if (front < sweepEnd)
            {
                continue;
            }
            attacks.RemoveAt(index);
        }
    }

    internal void ProcessSunBurns(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Sun orb attacks require the match lock.");
        }

        if (runtime.IsEnded)
        {
            return;
        }

        foreach (var victim in runtime.GetPlayers())
        {
            if (victim.StatusEffects.SunBurn is not { } burn)
            {
                continue;
            }
            if (nowUtc >= burn.NextTickAtUtc)
            {
                combatDamage.ApplyOrbShock(runtime, burn.OwnerId, burn.WeaponItemId, burn.Area, victim, nowUtc, Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER, isPeriodicDamage: true);
                burn = burn with { NextTickAtUtc = burn.NextTickAtUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS) };
                victim.StatusEffects.ApplySunBurn(burn);
            }
        }
    }

    internal void ProcessWindAttacks(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Wind orb attacks require the match lock.");
        }

        foreach (var attack in runtime.PendingWindAttacks)
        {
            if (runtime.IsEnded)
            {
                break;
            }

            var owner = runtime.GetPlayer(attack.OwnerId);
            if (owner == null || owner.IsEliminated)
            {
                continue;
            }

            var ownerArea = GameMapData.GetCurrentArea(owner.GameInfo.ObjectInfo.MapId, owner.GameInfo.ObjectInfo.Cell);
            var (monsters, players) = MatchOrbTarget.CollectTargetsInRadius(runtime, owner.PlayerId, ownerArea, attack.Position, attack.Radius);
            foreach (var monster in monsters)
            {
                combatDamage.ApplyMonsterHit(runtime, monster.MonsterId, owner.PlayerId, attack.SourceItemId, ownerArea, attack.Damage, nowUtc);
            }

            if (players.Count == 0)
            {
                continue;
            }

            foreach (var participant in players)
            {
                if (!participant.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, Config.SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS))
                {
                    // 플레이어 연타 방지
                    continue;
                }
                combatDamage.ApplyOrbShock(runtime, owner.PlayerId, attack.SourceItemId, ownerArea, participant, nowUtc);
                if (runtime.IsEnded)
                {
                    break;
                }
                if (participant.IsEliminated)
                {
                    continue;
                }
                participant.StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS));
                synchronization.QueueStatusEffect(runtime, participant, owner.PlayerId, ownerArea, CombatStatusEffectKind.WindOrbWound, Config.SWARM_WIND_WOUND_SECONDS);
            }
        }
        runtime.PendingWindAttacks.Clear();
    }
}
