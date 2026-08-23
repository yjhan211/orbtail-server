using game_server.network;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     바람 = 몸통박치기 (#232 2단계, 2026-08-17 저녁 유저 결정). 조준 투사체가 아니다: 바람 오브는 제자리(열 좌표)에서
///     오브를 따라 움직이다가, 감지 반경 안에 누가(몬스터·다른 플레이어) 들어오면 그쪽으로 한 번 몸을 던진다 —
///     뒤로 당겼다(예비 동작) 튀어나가 착지점 주변을 친다(스윙 리듬). 착지 반경 안 몬스터는 PvE 피해, 소유자 아닌
///     플레이어는 충격 1회(면역·소유자 초당 1회 창은 태양과 공용). 아무도 없으면 가만히 있다. 오브당 쿨다운.
///     파도가 자리에 깔리는 고정 범위 공격이라면 바람은 오브에 붙어 다니는 근접 공격이다.
///     연출은 클라(PlayerTool.PlayWindSlamCue): 오브 슬롯이 예비 동작 → 돌진 → 착지 순간 공용 오라 버스트 + 공명음 → 복귀.
///     판정은 착지 순간(예비+돌진 시간 뒤) 서버가 잠근 착지점에서 난다 — 표시 = 판정.
/// </summary>
public partial class GameServer
{
    // 7~15는 공격 사건 VFX(SwarmAttackEvents: 태양 시전 7 …)가 쓴다 — 겹치면 클라가 그쪽으로 삼킨다.
    // 16 = 몬스터를 향한 몸통박치기(VictimPlayerId 자리에 몬스터 id), 17 = 플레이어를 향한 것(VictimPlayerId = 플레이어 id).
    // 클라는 그 표적의 지금 자리로 돌진한다 — 표적이 움직여도 착지 = 표적 (서버 착지 판정도 표적의 그때 자리).
    private const int SwarmRingVfxKindWindSlamMonster = 16;
    private const int SwarmRingVfxKindWindSlamPlayer = 17;
    // 몸통 여유 — 교차사격과 같은 값.
    private const float SwarmWindSlamMonsterRadius = 0.3f;
    private const float SwarmWindSlamPlayerRadius = 0.25f;

    private sealed record PendingSwarmWindSlam(
        long MatchingId,
        long OwnerId,
        int WeaponItemId,
        int Ordinal,
        AreaType Area,
        Vector3f Point,
        long TargetCombatTargetId,
        long TargetPlayerId,
        int Damage,
        DateTime HitAtUtc);

    private readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), DateTime> _swarmWindSlamReadyAtUtc = new();
    private readonly List<PendingSwarmWindSlam> _pendingSwarmWindSlams = new();

    private void ProcessSwarmWindSlams(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;

        // 1) 착지: 예비 동작 + 돌진 시간이 지난 몸통박치기를 착지점에서 정산한다.
        for (int index = _pendingSwarmWindSlams.Count - 1; index >= 0; index--)
        {
            var slam = _pendingSwarmWindSlams[index];
            if (slam.MatchingId != matchingId || nowUtc < slam.HitAtUtc)
                continue;
            _pendingSwarmWindSlams.RemoveAt(index);
            monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);
            LandSwarmWindSlam(slam, nowUtc, monsters, participants, aliveSessions, aliveBots, allSessions);
        }

        // 2) 감지·발동: 바람 오브마다 쿨다운이 찼고 감지 반경 안에 누가 있으면 그쪽으로 몸을 던진다.
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
                if (!OrbData.TryGetColorAndTier(item.ItemId, out var color, out int tier) ||
                    color != OrbColor.Green)
                    continue;

                var key = (matchingId, owner.PlayerId, item.ItemUid);
                if (_swarmWindSlamReadyAtUtc.TryGetValue(key, out var readyAtUtc) && nowUtc < readyAtUtc)
                    continue;
                // 비무장(소환·채집 중)은 쉰다 — 미사일·물폭탄과 같은 규칙.
                if (!IsSwarmAttackArmed(matchingId, owner.PlayerId, nowUtc))
                    continue;

                tiers ??= GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var origin = GetSwarmOrbTrailPosition(matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                int tierIndex = Math.Clamp(tier, 1, 3) - 1;
                float triggerRadius = Config.SWARM_WIND_SLAM_TRIGGER_RADIUS_BY_TIER[tierIndex];

                monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);
                if (!TryFindSwarmWindSlamTarget(
                        owner, origin, triggerRadius, monsters, participants,
                        out Vector3f targetPoint, out long targetCombatTargetId, out long targetPlayerId,
                        out int targetMonsterId))
                    continue;

                if (sunMultiplier < 0f)
                    sunMultiplier = OrbData.GetSunPveAttackMultiplier(trailOrbs);
                int damage = Math.Max(1, (int)MathF.Round(
                    OrbData.GetSwarmPveAttackDamage(item.ItemId) * sunMultiplier *
                    Config.SWARM_WIND_SLAM_DAMAGE_MULTIPLIER));
                _swarmWindSlamReadyAtUtc[key] = nowUtc.AddSeconds(Config.SWARM_WIND_SLAM_COOLDOWN_SECONDS);
                _pendingSwarmWindSlams.Add(new PendingSwarmWindSlam(
                    matchingId, owner.PlayerId, item.ItemId, ordinal, owner.Area, targetPoint,
                    targetCombatTargetId, targetPlayerId, damage,
                    nowUtc.AddSeconds(Config.SWARM_WIND_SLAM_WINDUP_SECONDS + Config.SWARM_WIND_SLAM_LUNGE_SECONDS)));

                // 연출은 같은 구역 전원에게, 발동 순간 — 클라가 예비 동작 → 돌진 → 착지를 같은 시간표로 재생한다.
                // 표적을 실어 보내 클라가 그 표적의 지금 자리로 돌진하게 한다(움직여도 착지 = 표적).
                bool towardPlayer = targetPlayerId != 0;
                SendSwarmRingVfx(owner.Area, owner.PlayerId, targetPoint.X, targetPoint.Y,
                    Config.SWARM_WIND_SLAM_HIT_RADIUS, allSessions,
                    towardPlayer ? SwarmRingVfxKindWindSlamPlayer : SwarmRingVfxKindWindSlamMonster,
                    victimId: towardPlayer ? targetPlayerId : targetMonsterId, fromOrdinal: ordinal);
            }
        }
    }

    /// <summary>
    ///     감지 반경 안(바닥면 타원) 표적 — 소유자 아닌 플레이어가 하나라도 있으면 그중 가장 가까운 사람,
    ///     없으면 가장 가까운 몬스터 (2026-08-18 유저 지시 "반경 내 다른 플레이어가 있으면 최우선"). 없으면 false.
    /// </summary>
    private static bool TryFindSwarmWindSlamTarget(
        SpotArenaPlayerSpatial owner,
        Vector3f origin,
        float triggerRadius,
        IReadOnlyList<SwarmArenaCombatTarget> monsters,
        List<SpotArenaPlayerSpatial> participants,
        out Vector3f targetPoint,
        out long targetCombatTargetId,
        out long targetPlayerId,
        out int targetMonsterId)
    {
        targetPoint = origin;
        targetCombatTargetId = 0;
        targetPlayerId = 0;
        targetMonsterId = 0;
        float best = float.MaxValue;
        float radiusSquared = triggerRadius * triggerRadius;

        foreach (var participant in participants)
        {
            if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area)
                continue;
            float d = SwarmGroundDistanceSquared(origin, participant.Position);
            if (d > radiusSquared || d >= best)
                continue;
            best = d;
            targetPoint = participant.Position;
            targetCombatTargetId = 0;
            targetMonsterId = 0;
            targetPlayerId = participant.PlayerId;
        }

        // 사람이 반경 안에 있으면 몬스터는 보지 않는다.
        if (best < float.MaxValue)
            return true;

        foreach (var monster in monsters)
        {
            if (monster.Area != owner.Area)
                continue;
            float d = SwarmGroundDistanceSquared(origin, monster.Position);
            if (d > radiusSquared || d >= best)
                continue;
            best = d;
            targetPoint = monster.Position;
            targetCombatTargetId = monster.CombatTargetId;
            targetMonsterId = monster.MonsterId;
            targetPlayerId = 0;
        }

        return best < float.MaxValue;
    }

    private static float SwarmGroundDistanceSquared(Vector3f a, Vector3f b)
    {
        float dx = b.X - a.X;
        float dy = (b.Y - a.Y) * SwarmGroundYScale;
        return dx * dx + dy * dy;
    }

    /// <summary>착지 판정: 착지점 반경 안 몬스터 전부 PvE 피해(각각 치명 굴림), 소유자 아닌 플레이어 전부 충격(공용 창).</summary>
    private void LandSwarmWindSlam(
        PendingSwarmWindSlam slam,
        DateTime nowUtc,
        IReadOnlyList<SwarmArenaCombatTarget> monsters,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        float radius = Config.SWARM_WIND_SLAM_HIT_RADIUS;
        // 착지점 = 표적의 지금 자리 (표적이 살아 있으면). 죽었거나 사라졌으면 발동 순간의 자리.
        // (둘 다 record struct — FirstOrDefault의 기본값은 null이 아니라 빈 구조체라 직접 찾는다.)
        Vector3f point = slam.Point;
        if (slam.TargetCombatTargetId != 0)
        {
            foreach (var monster in monsters)
            {
                if (monster.CombatTargetId != slam.TargetCombatTargetId || monster.Position == null) continue;
                point = monster.Position;
                break;
            }
        }
        else if (slam.TargetPlayerId != 0)
        {
            foreach (var participant in participants)
            {
                if (participant.PlayerId != slam.TargetPlayerId || participant.Position == null) continue;
                point = participant.Position;
                break;
            }
        }

        int monsterHits = 0;
        foreach (var monster in monsters)
        {
            if (monster.Area != slam.Area)
                continue;
            if (!IsWithinSwarmGroundRadius(point, monster.Position, radius + SwarmWindSlamMonsterRadius))
                continue;

            monsterHits++;
            _swarmArenaManager.RecordMonsterAttackEvent(slam.MatchingId, monster.CombatTargetId);
            int monsterDamage = RollSwarmCriticalDamage(slam.Damage, out bool critical);
            ApplySwarmMonsterHitNow(
                slam.MatchingId, monster.CombatTargetId, monster.MonsterId, slam.OwnerId,
                slam.WeaponItemId, slam.Area, monsterDamage, critical, allSessions);
        }

        int shocks = 0;
        foreach (var participant in participants)
        {
            if (participant.PlayerId == slam.OwnerId || participant.Area != slam.Area)
                continue;
            if (!IsWithinSwarmGroundRadius(point, participant.Position, radius + SwarmWindSlamPlayerRadius))
                continue;
            if (!TryClaimSwarmShockWindow(slam.MatchingId, slam.OwnerId, participant.PlayerId, nowUtc))
                continue;

            shocks++;
            ApplySwarmShock(slam.MatchingId, slam.OwnerId, slam.WeaponItemId, slam.Area, participant.PlayerId,
                $"WIND_SLAM_HIT ordinal={slam.Ordinal}", aliveSessions, aliveBots, allSessions);
        }

        _gameEventLogManager.LogSystem(
            slam.MatchingId,
            $"WIND_SLAM owner={slam.OwnerId} ordinal={slam.Ordinal} at=({point.X:F2},{point.Y:F2}) " +
            $"radius={radius:F2} damage={slam.Damage} monsters={monsterHits} shocks={shocks}");
    }

    private void ClearSwarmWindSlamState(long matchingId)
    {
        foreach (var key in _swarmWindSlamReadyAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmWindSlamReadyAtUtc.Remove(key);
        _pendingSwarmWindSlams.RemoveAll(slam => slam.MatchingId == matchingId);
    }
}
