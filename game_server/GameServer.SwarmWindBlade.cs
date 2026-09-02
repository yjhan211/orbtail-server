using game_server.network;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     바람 = 회전 칼날 (#268). 바람 오브는 제자리(열 좌표)에서 돌며 반경 안 전원을 주기 틱으로 간다 — 믹서기.
///     반경 안 몬스터는 틱 PvE 피해, 소유자 아닌 플레이어는 충격 1회(피해자 0.9초 창 — 바람 전용, 태양에는
///     면역이 없다). 표적 선택·예고·돌진이 없다 — 판정 반경이 곧 무기다.
///     연출은 클라(PlayerTool.WindBlade): 오브 자전 + 판정 반경 칼날 원판 — 평시 저속, 적 감지 시
///     가속·발광. 서버는 별도 연출 패킷을 보내지 않는다 — 피해 틱의 피격 패킷이 곧 신호다.
/// </summary>
public partial class GameServer
{
    // 몸통 여유 — 교차사격과 같은 값.
    private const float SwarmWindBladeMonsterRadius = 0.3f;
    private const float SwarmWindBladePlayerRadius = 0.25f;

    // 바람만 피해자 면역 창을 둔다: 칼날은 예고선(회전 링)이 오브 위치 그대로라 표시=판정 어긋남이 없고, 면역까지
    // 걷히면 순수 연타 상향이 딸려온다 — 태양의 면역 제거는 침묵 관통 수리였지 연타 상향이 아니다.
    private const double SwarmWindBladeVictimImmuneSeconds = 0.9d;

    private void ProcessSwarmWindBlades(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;
        SwarmWindBladeState windBlade = GetSwarmMatchRuntime(matchingId).WindBlade;

        foreach (var owner in participants)
        {
            var trailOrbs = GetSwarmTrailOrbs(matchingId, owner.PlayerId);
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

                tiers ??= GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var origin = GetSwarmOrbTrailPosition(matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
                monsters ??= _swarmMonsterDirector.GetCombatTargets(matchingId);

                // 판정 전에 반경 안 표적부터 수집한다 — 시동 게이트가 표적 유무를 먼저 물어야 한다.
                List<SwarmArenaCombatTarget>? monstersInRadius = null;
                foreach (var monster in monsters)
                {
                    if (monster.Area != owner.Area)
                        continue;
                    if (!IsWithinSwarmGroundRadius(
                            origin, monster.Position, radius + SwarmWindBladeMonsterRadius))
                        continue;
                    (monstersInRadius ??= new List<SwarmArenaCombatTarget>()).Add(monster);
                }

                List<SwarmParticipantSpatial>? playersInRadius = null;
                foreach (var participant in participants)
                {
                    if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area)
                        continue;
                    if (!IsWithinSwarmGroundRadius(
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
                        _swarmMonsterDirector.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                        int monsterDamage = RollSwarmCriticalDamage(
                            matchingId, damage, out bool critical);
                        ApplySwarmMonsterHitNow(
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
                        ApplySwarmShock(matchingId, owner.PlayerId, item.ItemId, owner.Area, participant.PlayerId,
                            $"WIND_BLADE_HIT ordinal={ordinal}", aliveSessions, aliveBots, allSessions);
                        ApplySwarmWindWound(
                            windBlade, owner.PlayerId, owner.Area, participant.PlayerId,
                            nowUtc, aliveSessions);
                    }
                }

                // 빈 틱은 남기지 않는다 — 상시 무기라 매 틱 로그를 남기면 이벤트 흐름이 이것으로 찬다.
                if (monsterHits > 0 || shocks > 0)
                {
                    _gameEventLogManager.LogSystem(
                        matchingId,
                        $"WIND_BLADE owner={owner.PlayerId} ordinal={ordinal} at=({origin.X:F2},{origin.Y:F2}) " +
                        $"radius={radius:F2} damage={damage} monsters={monsterHits} shocks={shocks}");
                }
            }
        }
    }

}
