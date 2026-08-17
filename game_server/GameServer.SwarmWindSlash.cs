using game_server.network;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     바람 = 회전 칼날 (#232 2단계, 2026-08-17 저녁 유저 결정): 조준하지 않는다. 바람 오브는 제자리(열 좌표)에서
///     오브를 따라 움직이며, 주기마다 반경 안의 몬스터·플레이어를 칼날처럼 한 번에 벤다 — 몬스터는 PvE 피해,
///     소유자 아닌 플레이어는 충격 1회(피해자 면역·소유자 초당 1회 상한 그대로). 파도가 자리에 깔리는 고정 범위
///     공격이라면 바람은 오브에 붙어 다니는 이동 범위 공격이다. 연출은 옛 바람 공명 이펙트(회전 칼날)·공명음.
///     관통 칼날(직선 투사체)은 "너무 타겟팅"이라 퇴역.
/// </summary>
public partial class GameServer
{
    // 7~15는 공격 사건 VFX(SwarmAttackEvents: 태양 시전 7 …)가 쓴다 — 겹치면 클라가 그쪽으로 삼킨다.
    private const int SwarmRingVfxKindWindSlash = 16;
    // 몸통 여유 — 교차사격과 같은 값.
    private const float SwarmWindSlashMonsterRadius = 0.3f;
    private const float SwarmWindSlashPlayerRadius = 0.25f;

    private readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), DateTime> _swarmWindSlashNextAtUtc = new();

    private void ProcessSwarmWindSlashes(
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

            List<int>? tiers = null;
            float sunMultiplier = -1f;
            for (int ordinal = 0; ordinal < trailOrbs.Count; ordinal++)
            {
                var item = trailOrbs[ordinal];
                if (!SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var color, out int tier) ||
                    color != SurvivorOrbColor.Green)
                    continue;

                var key = (matchingId, owner.PlayerId, item.ItemUid);
                if (!_swarmWindSlashNextAtUtc.TryGetValue(key, out var nextAtUtc))
                {
                    // 첫 박자는 순번 지터로 흩는다 — 오브마다 제 박자 (일제사 회피, 사격 케이던스와 같은 규칙).
                    _swarmWindSlashNextAtUtc[key] = nowUtc.AddSeconds(
                        Config.SWARM_WIND_SLASH_INTERVAL_SECONDS * ResolveSwarmOrbCadenceJitter(ordinal));
                    continue;
                }

                if (nowUtc < nextAtUtc)
                    continue;
                _swarmWindSlashNextAtUtc[key] = nowUtc.AddSeconds(Config.SWARM_WIND_SLASH_INTERVAL_SECONDS);
                // 비무장(소환·채집 중)은 쉰다 — 미사일·물폭탄과 같은 규칙. 박자는 계속 간다.
                if (!IsSwarmAttackArmed(matchingId, owner.PlayerId, nowUtc))
                    continue;

                tiers ??= GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                if (sunMultiplier < 0f)
                    sunMultiplier = SurvivorOrbData.GetSunPveAttackMultiplier(trailOrbs);
                var center = GetSwarmOrbTrailPosition(matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                int tierIndex = Math.Clamp(tier, 1, 3) - 1;
                float radius = Config.SWARM_WIND_SLASH_RADIUS_BY_TIER[tierIndex];
                int damage = Math.Max(1, (int)MathF.Round(
                    SurvivorOrbData.GetSwarmPveAttackDamage(item.ItemId) * sunMultiplier *
                    Config.SWARM_WIND_SLASH_DAMAGE_MULTIPLIER));

                // 연출은 같은 구역 전원에게 — 판정과 같은 순간·같은 반경.
                SendSwarmRingVfx(owner.Area, owner.PlayerId, center.X, center.Y, radius, allSessions,
                    SwarmRingVfxKindWindSlash, victimId: 0, fromOrdinal: ordinal);

                monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);
                int monsterHits = 0;
                foreach (var monster in monsters)
                {
                    if (monster.Area != owner.Area)
                        continue;
                    if (!IsWithinSwarmGroundRadius(center, monster.Position, radius + SwarmWindSlashMonsterRadius))
                        continue;

                    monsterHits++;
                    _swarmArenaManager.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                    int monsterDamage = RollSwarmCriticalDamage(damage, out bool critical);
                    ApplySwarmMonsterHitNow(
                        matchingId, monster.CombatTargetId, monster.MonsterId, owner.PlayerId,
                        item.ItemId, owner.Area, monsterDamage, critical, allSessions);
                }

                int shocks = 0;
                foreach (var participant in participants)
                {
                    if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area)
                        continue;
                    if (!IsWithinSwarmGroundRadius(center, participant.Position, radius + SwarmWindSlashPlayerRadius))
                        continue;
                    if (!TryClaimSwarmShockWindow(matchingId, owner.PlayerId, participant.PlayerId, nowUtc))
                        continue;

                    shocks++;
                    ApplySwarmShock(matchingId, owner.PlayerId, item.ItemId, owner.Area, participant.PlayerId,
                        $"WIND_SLASH ordinal={ordinal}", aliveSessions, aliveBots, allSessions);
                }

                if (monsterHits > 0 || shocks > 0)
                {
                    _gameEventLogManager.LogSystem(
                        matchingId,
                        $"WIND_SLASH owner={owner.PlayerId} ordinal={ordinal} tier={tier} radius={radius:F2} " +
                        $"at=({center.X:F2},{center.Y:F2}) damage={damage} monsters={monsterHits} shocks={shocks}");
                }
            }
        }
    }

    private void ClearSwarmWindSlashState(long matchingId)
    {
        foreach (var key in _swarmWindSlashNextAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmWindSlashNextAtUtc.Remove(key);
    }
}
