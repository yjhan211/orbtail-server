using game_server.network;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     바람 = 회전 칼날 (#268, 2026-08-25 유저 결정). 바람 오브는 제자리(열 좌표)에서 돌며 반경 안
///     전원을 주기 틱으로 간다 — 믹서기. 반경 안 몬스터는 틱 PvE 피해, 소유자 아닌 플레이어는
///     충격 1회(면역 창은 태양과 공용). 표적 선택·예고·돌진이 없다 — 판정 반경이 곧 무기다.
///     몸통박치기 세대(감지→돌진→착지, 2026-08-17)는 퇴역: 감지 대기가 병목이라 실효 간격이
///     3.5초였고, 3박자 연출로도 직관적으로 읽히지 않았다.
///     연출은 클라(PlayerTool.WindBlade): 오브 자전 + 판정 반경 칼날 원판 — 평시 저속, 적 감지 시
///     가속·발광. 서버는 별도 연출 패킷을 보내지 않는다 — 피해 틱의 피격 패킷이 곧 신호다.
/// </summary>
public partial class GameServer
{
    // 몸통 여유 — 교차사격과 같은 값.
    private const float SwarmWindBladeMonsterRadius = 0.3f;
    private const float SwarmWindBladePlayerRadius = 0.25f;

    private readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), DateTime>
        _swarmWindBladeNextTickAtUtc = new();

    private void ProcessSwarmWindBlades(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;

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

                var key = (matchingId, owner.PlayerId, item.ItemUid);
                if (_swarmWindBladeNextTickAtUtc.TryGetValue(key, out var nextTickAtUtc) && nowUtc < nextTickAtUtc)
                    continue;
                // 비무장(소환·채집 중)은 쉰다 — 미사일·물폭탄과 같은 규칙. 틱 시계는 계속 돈다.
                _swarmWindBladeNextTickAtUtc[key] = nowUtc.AddSeconds(Config.SWARM_WIND_BLADE_TICK_SECONDS);
                if (!IsSwarmAttackArmed(matchingId, owner.PlayerId, nowUtc))
                    continue;

                tiers ??= GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var origin = GetSwarmOrbTrailPosition(matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                float radius = Config.SWARM_WIND_BLADE_RADIUS_BY_TIER[Math.Clamp(tier, 1, 3) - 1];
                monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);

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

                List<SpotArenaPlayerSpatial>? playersInRadius = null;
                foreach (var participant in participants)
                {
                    if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area)
                        continue;
                    if (!IsWithinSwarmGroundRadius(
                            origin, participant.Position, radius + SwarmWindBladePlayerRadius))
                        continue;
                    (playersInRadius ??= new List<SpotArenaPlayerSpatial>()).Add(participant);
                }

                // 시동 게이트는 퇴역 (2026-08-25 유저 정정 "돌면 그냥 데미지"): 서버 0.9초 게이트 +
                // 틱 정렬(0.7초)이 겹쳐 체감 1.4초 지연이었다 — 회전 시동은 클라 연출만 지고,
                // 판정은 반경 안에 표적이 있으면 즉시 틱이 나간다.
                if (monstersInRadius == null && playersInRadius == null)
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
                        _swarmArenaManager.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                        int monsterDamage = RollSwarmCriticalDamage(damage, out bool critical);
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
                        if (!TryClaimSwarmShockWindow(matchingId, owner.PlayerId, participant.PlayerId, nowUtc))
                            continue;

                        shocks++;
                        ApplySwarmShock(matchingId, owner.PlayerId, item.ItemId, owner.Area, participant.PlayerId,
                            $"WIND_BLADE_HIT ordinal={ordinal}", aliveSessions, aliveBots, allSessions);
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

    private void ClearSwarmWindBladeState(long matchingId)
    {
        foreach (var key in _swarmWindBladeNextTickAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmWindBladeNextTickAtUtc.Remove(key);
    }
}
