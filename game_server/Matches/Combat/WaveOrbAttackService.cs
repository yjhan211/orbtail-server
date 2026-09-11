using game_server.matches;
using game_server.matches.logging;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches.combat;

/// <summary>
/// 매치에 생성된 파도 공격을 예고 시간 후 기폭하고 피해와 감속을 적용한다.
/// 공격자가 탈락해도 진행하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class WaveOrbAttackService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    GameEventLogManager eventLogs)
{
    public void ProcessTick(
        MatchRuntime runtime,
        DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Wave orb attacks require the match lock.");
        if (runtime.IsEnded) return;
        // 1) 기폭: 예약된 소용돌이 정산.
        for (int index = runtime.PendingWaveAttacks.Count - 1; index >= 0; index--)
        {
            var vortex = runtime.PendingWaveAttacks[index];
            if (nowUtc < vortex.ExplodeAtUtc)
                continue;
            runtime.PendingWaveAttacks.RemoveAt(index);
            Detonate(runtime, vortex.OwnerId, vortex.Area, vortex.Position,
                vortex.Damage, vortex.Radius, vortex.SourceItemId, nowUtc);
            if (runtime.IsEnded) return;
        }

    }

    /// <summary>
    ///     소용돌이 기폭 (#268): 반경 안 전원(몹·플레이어 동일)에게 타격 피드백 수준의 피해와
    ///     "침수"(5초 25% 감속) 디버프를 준다. 변위(당김·밀침·원 밖 축출) 실험은 전부
    ///     기각(유저 판정). 겹친 공격은 각각 피해를 적용하고 감속 지속 시간을 갱신한다.
    /// </summary>
    internal void Detonate(
        MatchRuntime runtime,
        long ownerId,
        AreaType area,
        Vector3f position,
        int damage,
        float radius,
        int sourceItemId,
        DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Wave orb attacks require the match lock.");
        if (runtime.IsEnded) return;
        long matchingId = runtime.MatchingId;
        var players = runtime.GetAlivePlayers();
        var owner = runtime.GetParticipant(ownerId);
        float radiusSquared = radius * radius;
        int hitCount = 0;
        int notifiedCount = 0;
        // 몹: 착탄 지연 정산 파이프라인 재사용 — 킬 보상·상태 브로드캐스트가 따라온다.
        foreach (var target in runtime.Monsters.GetCombatTargets())
        {
            if (target.Area != area)
                continue;
            float dx = target.Position.X - position.X;
            float dy = (target.Position.Y - position.Y) * 2f;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, damage, out bool critical);
            runtime.Monsters.ReserveMonsterDamage(target.CombatTargetId, monsterDamage);
            runtime.Monsters.RecordMonsterAttackEvent(target.CombatTargetId);
            combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(
                target.CombatTargetId, ownerId, monsterDamage, nowUtc));
            runtime.Monsters.TrySlowMonster(target.CombatTargetId, OrbData.WaveSlowSeconds, nowUtc);
            hitCount++;

            int monsterId = runtime.Monsters.GetMonsterIdForCombatTarget(target.CombatTargetId);
            if (monsterId <= 0)
                continue;

            notifiedCount++;
            combatDamage.SendMonsterHitNotification(runtime, owner,
                monsterId, area, sourceItemId, monsterDamage, critical, showDamageOnly: true);
        }

        // 플레이어: 같은 반경(바닥면 타원) + 몸통 여유. 소유자 제외 — 침수 디버프 + 피해.
        int soaked = 0;
        foreach (var participant in players)
        {
            if (participant.IsEliminated || participant.PlayerId == ownerId || participant.CurrentArea != area || participant.Position == null)
                continue;
            if (!SwarmCombatGeometry.IsWithinGroundRadius(position, participant.Position, radius + SwarmCombatGeometry.PlayerRadius))
                continue;

            // 충격 면역 없음: 겹친 링에 다 맞는다 — 침수는 지속 갱신이라 중첩 무해.
            soaked++;
            combatDamage.ApplySwarmShock(runtime, healthService, ownerId, sourceItemId, area, participant.PlayerId,
                "WAVE_VORTEX_HIT", players,
                Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);

            if (runtime.IsEnded) return;
            if (participant.IsEliminated) continue;
            participant.WaveSlowUntilUtc = nowUtc.AddSeconds(OrbData.WaveSlowSeconds);
            var victimSession = participant.Session;
            if (victimSession != null && ownerId != 0)
            {
                using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
                {
                    SourcePlayerId = ownerId,
                    TargetPlayerId = participant.PlayerId,
                    AreaType = area,
                    Effect = CombatStatusEffectKind.WaveOrbSlow,
                    DurationMs = (int)(OrbData.WaveSlowSeconds * 1000f)
                });
                victimSession.TrySend(packet);
            }
        }

        if (hitCount > 0 || soaked > 0)
        {
            eventLogs.LogSystem(
                matchingId,
                $"wave_vortex_hit owner={ownerId} area={area} monsters={hitCount} " +
                $"notified={notifiedCount} playersSoaked={soaked} radius={radius:F2} " +
                $"damage={damage} item={sourceItemId}");
        }
    }


}
