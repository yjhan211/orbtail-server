using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>
///     바람 오브의 반경·시동 시간·공격 주기를 검사하고 몬스터 피해와 플레이어 충격·상처를 적용한다.
///     시계와 면역 상태는 매치가 소유하며 매치 잠금 안에서 실행한다.
///     공통 피해 적용은 MatchCombatDamageService에 맡긴다.
/// </summary>
internal sealed class WindBladeService(
    MatchRuntimeStore matchRuntimes,
    OrbTrailService orbTrails,
    MatchCombatDamageService combatDamage,
    GameEventLogManager eventLogs)
{
    // 몸통 여유 — 교차사격과 같은 값.

    private const float SwarmWindBladePlayerRadius = 0.25f;

    // 바람만 피해자 면역 창을 둔다: 칼날은 예고선(회전 링)이 오브 위치 그대로라 표시=판정 어긋남이 없고, 면역까지
    // 걷히면 순수 연타 상향이 딸려온다 — 태양의 면역 제거는 침묵 관통 수리였지 연타 상향이 아니다.
    // 원천은 swarm_config.csv (#335) — 미등재 시 코드 기본값.
    private static double SwarmWindBladeVictimImmuneSeconds =>
        SwarmConfigData.GetDouble("SWARM_WIND_BLADE_VICTIM_IMMUNE_SECONDS", 0.9d);

    public void Process(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;
        SwarmWindBladeState windBlade = matchRuntimes.GetRequired(matchingId).Swarm.WindBlade;

        foreach (var owner in participants)
        {
            var trailOrbs = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(owner.PlayerId).GetOrderedOrbs();
            if (trailOrbs.Count == 0)
                continue;

            float sunMultiplier = -1f;
            List<int>? tiers = null;
            for (int ordinal = 0; ordinal < trailOrbs.Count; ordinal++)
            {
                var item = trailOrbs[ordinal];
                if (!OrbData.TryGetColorAndTier(item.ItemId, out var color, out int tier) ||
                    color != OrbColor.Green)
                    continue;

                if (!windBlade.TryBeginTick(
                        owner.PlayerId, item.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_TICK_SECONDS))
                    continue;

                tiers ??= orbTrails.GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var origin = orbTrails.GetSwarmOrbTrailPosition(matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
                monsters ??= matchRuntimes.GetRequired(matchingId).Monsters.GetCombatTargets(matchingId);

                // 판정 전에 반경 안 표적부터 수집한다 — 시동 게이트가 표적 유무를 먼저 물어야 한다.
                List<SwarmArenaCombatTarget>? monstersInRadius = null;
                foreach (var monster in monsters)
                {
                    if (monster.Area != owner.Area)
                        continue;
                    if (!SwarmCombatGeometry.IsWithinGroundRadius(
                            origin, monster.Position, radius + SwarmCombatGeometry.MonsterRadius))
                        continue;
                    (monstersInRadius ??= new List<SwarmArenaCombatTarget>()).Add(monster);
                }

                List<SwarmParticipantSpatial>? playersInRadius = null;
                foreach (var participant in participants)
                {
                    if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area)
                        continue;
                    if (!SwarmCombatGeometry.IsWithinGroundRadius(
                            origin, participant.Position, radius + SwarmWindBladePlayerRadius))
                        continue;
                    (playersInRadius ??= new List<SwarmParticipantSpatial>()).Add(participant);
                }

                // 짧은 시동 게이트: "즉시 틱"은 오브가 돌기도 전에 피해가 들어가 어색하다 — 표적이 반경에 든 순간부터
                // 클라 회전 20% 도달에 맞춘 0.2초만 기다린다. 반경이 비면 리셋.
                if (monstersInRadius == null && playersInRadius == null)
                {
                    windBlade.ResetEngagement(owner.PlayerId, item.ItemUid);
                    continue;
                }

                if (!windBlade.HasCompletedSpinup(
                        owner.PlayerId, item.ItemUid, nowUtc, Config.SWARM_WIND_BLADE_SPINUP_SECONDS))
                    continue;

                if (sunMultiplier < 0f)
                    sunMultiplier = OrbData.GetSunPveAttackMultiplier(trailOrbs);
                int damage = Math.Max(1, (int)MathF.Round(
                    OrbData.GetSwarmPveAttackDamage(item.ItemId) * sunMultiplier *
                    Config.SWARM_WIND_BLADE_DAMAGE_MULTIPLIER));

                int monsterHits = 0;
                if (monstersInRadius != null)
                {
                    foreach (var monster in monstersInRadius)
                    {
                        monsterHits++;
                        matchRuntimes.GetRequired(matchingId).Monsters.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                        int monsterDamage = combatDamage.RollSwarmCriticalDamage(
                            matchingId, damage, out bool critical);
                        combatDamage.ApplySwarmMonsterHitNow(
                            matchingId, monster.CombatTargetId, monster.MonsterId, owner.PlayerId,
                            item.ItemId, owner.Area, monsterDamage, critical, allSessions);
                    }
                }

                int shocks = 0;
                if (playersInRadius != null)
                {
                    foreach (var participant in playersInRadius)
                    {
                        if (!windBlade.TryClaimVictimShock(
                                participant.PlayerId, nowUtc, SwarmWindBladeVictimImmuneSeconds))
                            continue;

                        shocks++;
                        // 충격 먼저, 상처는 그다음 — 상처를 낸 그 틱이 자기 충격에 치명타를 걸지 않게.
                        combatDamage.ApplySwarmShock(matchingId, owner.PlayerId, item.ItemId, owner.Area, participant.PlayerId,
                            $"WIND_BLADE_HIT ordinal={ordinal}", aliveSessions, aliveBots, allSessions);
                        ApplySwarmWindWound(
                            windBlade, owner.PlayerId, owner.Area, participant.PlayerId,
                            nowUtc, aliveSessions);
                    }
                }

                // 빈 틱은 남기지 않는다 — 상시 무기라 매 틱 로그를 남기면 이벤트 흐름이 이것으로 찬다.
                if (monsterHits > 0 || shocks > 0)
                {
                    eventLogs.LogSystem(
                        matchingId,
                        $"WIND_BLADE owner={owner.PlayerId} ordinal={ordinal} at=({origin.X:F2},{origin.Y:F2}) " +
                        $"radius={radius:F2} damage={damage} monsters={monsterHits} shocks={shocks}");
                }
            }
        }
    }

    /// <summary>상처 부여·갱신 — HUD 통지 포함. 효과는 ApplySwarmShock의 치명타 굴림이 읽는다.</summary>
    private void ApplySwarmWindWound(
        SwarmWindBladeState windBlade, long ownerId, AreaType area, long victimId,
        DateTime nowUtc, List<GameClientSession> aliveSessions)
    {
        windBlade.ApplyWound(victimId, nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS));
        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        if (victimSession == null || !victimSession.PlayerId.HasValue || ownerId == 0) return;

        using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
        {
            SourcePlayerId = ownerId,
            TargetPlayerId = victimId,
            AreaType = area,
            Effect = CombatStatusEffectKind.WindWound,
            DurationMs = (int)(Config.SWARM_WIND_WOUND_SECONDS * 1000f)
        });
        victimSession.TrySend(packet);
    }

}
