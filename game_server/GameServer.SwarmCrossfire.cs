using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

/// <summary>
///     교차사격 (#232 2단계). 오브는 몬스터만 조준하지만, 태양의 공격은 유도탄이 아니라
///     "경고색이 깜빡인 뒤 큰 공격이 한 번에 천천히 지나가는" 직선이다 (2026-08-17 유저 판정).
///     발사 순간 원점(오브)·기준점(몬스터)으로 직선을 잠그고, 예고 시간 뒤 판정 앞머리가
///     원점에서 끝까지 일정 속도로 쓸고 지나간다 — 지나간 자리의 몬스터는 PvE 피해(관통),
///     다른 플레이어는 고정 충격 1회. 예고 뒤 몬스터가 죽어도 모양은 잠근 위치를 끝까지 쓴다.
///     P0-A는 태양만 — 바람 유도탄·파도 물폭탄은 종전대로.
/// </summary>
public partial class GameServer
{
    private static readonly bool SwarmCrossfireEnabled = true;

    // 아이소 바닥면 정규화 계수 — 접촉 판정·물폭탄 반경과 같은 dy×2.
    private const float SwarmGroundYScale = 2f;
    // 몬스터 몸통 여유 — 앞머리가 몸 가장자리를 스쳐도 맞는다 (접촉 반경 0.32와 같은 급).
    private const float SwarmCrossfireMonsterRadius = 0.3f;

    private sealed class SwarmCrossfireShape
    {
        public long MatchingId { get; init; }
        public long EventId { get; init; }
        public long OwnerId { get; init; }
        public int WeaponItemId { get; init; }
        public int Damage { get; init; }
        public AreaType Area { get; init; }
        // 원점·끝은 월드 좌표. 판정은 바닥면(Y×2)에서 한다.
        public Vector3f Origin { get; init; } = new(0f, 0f, 0f);
        public Vector3f End { get; init; } = new(0f, 0f, 0f);
        public float GroundLength { get; init; }
        public float HalfWidth { get; init; }
        public DateTime ArmedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public int AnchorMonsterId { get; init; }
        // 앞머리가 지난 축 위치(바닥면 단위, 원점 = 0). 캡 반폭 앞에서 시작한다.
        public float LastFront { get; set; }
        public HashSet<long> HitVictims { get; } = new();
        public HashSet<long> HitMonsters { get; } = new();
    }

    private readonly List<SwarmCrossfireShape> _swarmCrossfireShapes = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireVictimImmuneUntilUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireOwnerLastHitAtUtc = new();
    private long _swarmCrossfireEventSeq;

    /// <summary>이 발사가 교차사격 모양(태양 직선)으로 처리되는가 — 유도탄 경로를 대체한다.</summary>
    private static bool IsSwarmCrossfireWeapon(int weaponItemId) =>
        SwarmCrossfireEnabled &&
        SurvivorOrbData.TryGetColorAndTier(weaponItemId, out var color, out _) &&
        color == SurvivorOrbColor.Red;

    /// <summary>
    ///     발사 순간 직선을 잠근다. 소유자당 동시 예고 상한을 넘으면 false — 호출부가 기준 몬스터에
    ///     모양 없이 직접 피해를 준다(화력 보존, 화면 포화 방지).
    /// </summary>
    private bool TryScheduleSwarmCrossfire(
        long matchingId,
        ProximityCombatAttack attack,
        Vector3f? origin,
        Vector3f? anchor,
        int anchorMonsterId,
        int damage,
        DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        if (origin == null || anchor == null || !IsSwarmCrossfireWeapon(attack.WeaponItemId))
            return false;
        SurvivorOrbData.TryGetColorAndTier(attack.WeaponItemId, out _, out int tier);

        int activeForOwner = 0;
        foreach (var shape in _swarmCrossfireShapes)
        {
            if (shape.MatchingId == matchingId && shape.OwnerId == attack.AttackerPlayerId)
                activeForOwner++;
        }

        if (activeForOwner >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
            return false;

        // 바닥면 기하 (2026-08-17 유저 판정: 범위가 타일을 따라가야 한다). 이 맵은 아이소 타일이라
        // 월드 Y가 화면 세로로 절반 눌려 있다 — 접촉·물폭탄과 같은 정규화(dy×2)로 바닥면에서
        // 방향·연장·폭을 재고, 월드로 되돌려 보낸다. 클라도 같은 면(Y 0.5 스케일)에 그린다.
        float gx = anchor.X - origin.X;
        float gy = (anchor.Y - origin.Y) * SwarmGroundYScale;
        float anchorDistance = MathF.Sqrt(gx * gx + gy * gy);
        if (anchorDistance < 0.05f)
            return false;

        int tierIndex = Math.Clamp(tier, 1, 3) - 1;
        float extend = Config.SWARM_CROSSFIRE_SUN_EXTEND_BY_TIER[tierIndex];
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        float groundLength = anchorDistance + extend;
        var end = new Vector3f(
            anchor.X + gx / anchorDistance * extend,
            anchor.Y + gy / anchorDistance * extend / SwarmGroundYScale,
            0f);

        // 앞머리는 원점 앞 캡(반폭)에서 출발해 끝 너머 캡까지 간다 — 캡슐 전체를 한 번 쓴다.
        float halfWidth = width * 0.5f;
        float sweepSeconds = (groundLength + width) / Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
        long eventId = ++_swarmCrossfireEventSeq;
        var armedAt = nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS);
        _swarmCrossfireShapes.Add(new SwarmCrossfireShape
        {
            MatchingId = matchingId,
            EventId = eventId,
            OwnerId = attack.AttackerPlayerId,
            WeaponItemId = attack.WeaponItemId,
            Damage = damage,
            Area = attack.Area,
            Origin = new Vector3f(origin.X, origin.Y, 0f),
            End = end,
            GroundLength = groundLength,
            HalfWidth = halfWidth,
            ArmedAtUtc = armedAt,
            ExpiresAtUtc = armedAt.AddSeconds(sweepSeconds),
            AnchorMonsterId = anchorMonsterId,
            LastFront = -halfWidth
        });

        BroadcastSwarmCrossfireTelegraph(
            eventId, attack, origin, end, width, sweepSeconds, anchorMonsterId, allSessions);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_TELEGRAPH event={eventId} owner={attack.AttackerPlayerId} " +
            $"weapon={attack.WeaponItemId} tier={tier} shape=line anchor={anchorMonsterId} " +
            $"area={attack.Area} origin=({origin.X:F2},{origin.Y:F2}) end=({end.X:F2},{end.Y:F2}) " +
            $"width={width:F2} groundLength={groundLength:F2} damage={damage} " +
            $"telegraph={Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS:F2} sweep={sweepSeconds:F2}");
        return true;
    }

    private static void BroadcastSwarmCrossfireTelegraph(
        long eventId,
        ProximityCombatAttack attack,
        Vector3f origin,
        Vector3f end,
        float width,
        float sweepSeconds,
        int anchorMonsterId,
        List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_CROSSFIRE_TELEGRAPH);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_CROSSFIRE_TELEGRAPH
        {
            EventId = eventId,
            OwnerPlayerId = attack.AttackerPlayerId,
            WeaponItemId = attack.WeaponItemId,
            Shape = Config.SWARM_CROSSFIRE_SHAPE_LINE,
            OriginX = origin.X,
            OriginY = origin.Y,
            EndX = end.X,
            EndY = end.Y,
            Width = width,
            TelegraphSeconds = Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS,
            // 판정 창 = 앞머리가 캡슐 전체를 쓸고 지나가는 시간. 클라 앞머리 연출이 같은 시간을 쓴다.
            ActiveSeconds = sweepSeconds,
            AnchorMonsterId = anchorMonsterId,
            OwnerOrbOrdinal = attack.AttackerTrailOrdinal
        }));
        // 같은 구역 전원 — 소유자도 받는다. 자기 모양이 어디 생겼는지 봐야 다음 자리를 고른다.
        foreach (var session in allSessions)
        {
            if (session.PlayerId.HasValue && !session.IsEliminated && session.CurrentArea == attack.Area)
                session.Send(packet);
        }
    }

    /// <summary>
    ///     예고가 끝난 모양의 앞머리를 전진시키며 판정한다. 이번 틱에 앞머리가 지난 축 구간
    ///     [지난 앞머리, 현재 앞머리] × 반폭 안에 있는 몬스터는 PvE 피해(관통, 사건당 1회),
    ///     소유자 아닌 플레이어는 충격 1회 — 피해자는 전역 면역, 소유자는 초당 1회 상한.
    /// </summary>
    private void ProcessSwarmCrossfires(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        if (!SwarmCrossfireEnabled)
            return;

        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;
        for (int index = _swarmCrossfireShapes.Count - 1; index >= 0; index--)
        {
            var shape = _swarmCrossfireShapes[index];
            if (shape.MatchingId != matchingId)
                continue;
            if (nowUtc < shape.ArmedAtUtc)
                continue;

            float sweepEnd = shape.GroundLength + shape.HalfWidth;
            float front = nowUtc >= shape.ExpiresAtUtc
                ? sweepEnd
                : -shape.HalfWidth +
                  (float)(nowUtc - shape.ArmedAtUtc).TotalSeconds * Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
            front = MathF.Min(front, sweepEnd);
            float lastFront = shape.LastFront;
            shape.LastFront = front;

            // 몬스터 — 관통. 같은 구역, 사건당 한 번.
            monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);
            foreach (var monster in monsters)
            {
                if (monster.Area != shape.Area || shape.HitMonsters.Contains(monster.CombatTargetId))
                    continue;
                if (!IsPointSweptBySwarmCrossfire(shape, monster.Position, lastFront, front, SwarmCrossfireMonsterRadius))
                    continue;

                shape.HitMonsters.Add(monster.CombatTargetId);
                _swarmArenaManager.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                int monsterDamage = RollSwarmCriticalDamage(shape.Damage, out bool critical);
                ApplySwarmMonsterHitNow(
                    matchingId, monster.CombatTargetId, monster.MonsterId, shape.OwnerId,
                    shape.WeaponItemId, shape.Area, monsterDamage, critical, allSessions);
            }

            // 플레이어 — 충격.
            foreach (var participant in participants)
            {
                if (participant.PlayerId == shape.OwnerId || participant.Area != shape.Area)
                    continue;
                if (shape.HitVictims.Contains(participant.PlayerId))
                    continue;
                if (_swarmCrossfireVictimImmuneUntilUtc.TryGetValue(
                        (matchingId, participant.PlayerId), out var immuneUntil) &&
                    nowUtc < immuneUntil)
                    continue;
                if (_swarmCrossfireOwnerLastHitAtUtc.TryGetValue(
                        (matchingId, shape.OwnerId), out var ownerLastHit) &&
                    (nowUtc - ownerLastHit).TotalSeconds < Config.SWARM_CROSSFIRE_OWNER_HIT_INTERVAL_SECONDS)
                    continue;
                if (!IsPointSweptBySwarmCrossfire(shape, participant.Position, lastFront, front, 0f))
                    continue;

                shape.HitVictims.Add(participant.PlayerId);
                _swarmCrossfireVictimImmuneUntilUtc[(matchingId, participant.PlayerId)] =
                    nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_VICTIM_IMMUNE_SECONDS);
                _swarmCrossfireOwnerLastHitAtUtc[(matchingId, shape.OwnerId)] = nowUtc;
                ApplySwarmCrossfireShock(matchingId, shape, participant.PlayerId, aliveSessions, aliveBots, allSessions);
            }

            if (front >= sweepEnd)
                _swarmCrossfireShapes.RemoveAt(index);
        }
    }

    /// <summary>
    ///     쓸기 판정 (바닥면): 점을 축에 사영한 위치가 이번 틱 앞머리 구간 안이고, 축에서의
    ///     수직 거리가 반폭(+여유) 이하. 캡슐 양 끝은 캡 반폭만큼 축 구간을 늘려 판정한다.
    /// </summary>
    private static bool IsPointSweptBySwarmCrossfire(
        SwarmCrossfireShape shape, Vector3f point, float fromFront, float toFront, float radiusPadding)
    {
        float ax = shape.Origin.X, ay = shape.Origin.Y * SwarmGroundYScale;
        float bx = shape.End.X, by = shape.End.Y * SwarmGroundYScale;
        float px = point.X, py = point.Y * SwarmGroundYScale;
        float abx = bx - ax, aby = by - ay;
        float length = shape.GroundLength;
        if (length <= 0f)
            return false;
        float ux = abx / length, uy = aby / length;
        float along = (px - ax) * ux + (py - ay) * uy;
        float perpendicular = MathF.Abs((px - ax) * uy - (py - ay) * ux);
        float reach = shape.HalfWidth + radiusPadding;
        if (perpendicular > reach)
            return false;
        // 캡슐: 축 밖(원점 앞·끝 너머)은 캡 반지름 안이어야 한다.
        float clampedAlong = Math.Clamp(along, 0f, length);
        float dxToSegment = along - clampedAlong;
        if (dxToSegment * dxToSegment + perpendicular * perpendicular > reach * reach)
            return false;
        return along > fromFront && along <= toFront + radiusPadding;
    }

    /// <summary>
    ///     즉시 몬스터 타격 (교차사격 쓸기·상한 초과 폴백 공용). 착탄 지연 큐와 같은 정산 —
    ///     결과 집계, 처치 계측(monster_lifetime), 처치 로그, 소환석 드롭. 공격자 화면엔 숫자만
    ///     띄운다(투사체 없음).
    /// </summary>
    private void ApplySwarmMonsterHitNow(
        long matchingId,
        long combatTargetId,
        int monsterId,
        long attackerId,
        int weaponItemId,
        AreaType area,
        int damage,
        bool critical,
        List<GameClientSession> allSessions)
    {
        var damageResult = _swarmArenaManager.ApplyMonsterDamage(matchingId, combatTargetId, attackerId, damage);
        if (!damageResult.Applied)
            return;

        _gameEventLogManager.RecordSurvivorMonsterHit(matchingId, attackerId, damage, damageResult.Killed);
        var attackerSession = allSessions.FirstOrDefault(session => session.PlayerId == attackerId);
        attackerSession?.SendEmotionAfterimageMonsterAttackFeedback(
            monsterId, area, weaponItemId, damage, critical, noProjectile: true);

        if (damageResult.Killed && damageResult.MonsterState != null)
            SettleSwarmMonsterKill(matchingId, damageResult, attackerId, damage, allSessions);
    }

    /// <summary>처치 정산 — 착탄 큐와 즉시 타격이 같은 경로를 쓴다.</summary>
    private void SettleSwarmMonsterKill(
        long matchingId,
        SwarmArenaDamageResult damageResult,
        long attackerId,
        int damage,
        List<GameClientSession> allSessions)
    {
        if (damageResult.MonsterState == null)
            return;

        // 기준점 계측 (#232 1단계): 종·생존초·살아 있는 동안 받은 공격 사건 수. 완료 조건
        // "몬스터당 공격 모양 평균 2회 이상"과 "즉시 지워져 기준점이 못 되는 몹"을 여기서 잰다.
        _gameEventLogManager.LogSystem(
            matchingId,
            $"monster_lifetime kind={damageResult.Kind} area={damageResult.MonsterState.AreaType} " +
            $"aliveSeconds={damageResult.AliveSeconds:F1} attackEvents={damageResult.AttackEventCount} " +
            $"killer={attackerId}");

        // 처치 계측 (#226 E): 종·구역·처치자 — 요약의 몹 처치 지표가 이 이벤트를 읽는다.
        _gameEventLogManager.LogEmotionAfterimageKilled(
            matchingId, damageResult.MonsterId,
            damageResult.MonsterState.AreaType.ToString(),
            isCore: damageResult.Kind == SwarmMonsterKind.RunawayGoblin,
            firstAttackerPlayerId: attackerId,
            lastAttackerPlayerId: attackerId,
            new Dictionary<long, int> { [attackerId] = damage });
        SpawnSpotArenaSummonStone(
            matchingId, damageResult.MonsterState, allSessions,
            damageResult.HeartReward, damageResult.BootsReward, damageResult.KeyReward);
    }

    private void ApplySwarmCrossfireShock(
        long matchingId,
        SwarmCrossfireShape shape,
        long victimId,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        int shock = Config.SWARM_CROSSFIRE_SHOCK_CORRUPTION;
        int corruptionBefore;
        int corruptionAfter;

        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        if (victimSession != null)
        {
            corruptionBefore = victimSession.CurrentCorruption;
            // 사격 피격 경로 재사용 — 오염 증가·피격 숫자·탈락 흐름이 그대로 따라온다.
            victimSession.ApplyProximityAutoCombatHit(shape.OwnerId, shape.Area, shape.WeaponItemId, shock);
            corruptionAfter = victimSession.CurrentCorruption;
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == victimId);
            if (bot == null)
                return;
            corruptionBefore = bot.Corruption;
            bot.LastProximityAttackerPlayerId = shape.OwnerId;
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            _gameEventLogManager.LogSurvivorHit(
                matchingId, shape.OwnerId, bot.PlayerId, shape.WeaponItemId, shock,
                bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                bot.Corruption + shock >= Config.SURVIVOR_MAX_CORRUPTION,
                BotPlayerManager.IsBotPlayerId(shape.OwnerId), DateTimeOffset.UtcNow);
            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + shock);
            corruptionAfter = bot.Corruption;
        }

        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == shape.OwnerId);
        ownerSession?.SendProximityAutoCombatAttackFeedback(victimId, shape.Area, shape.WeaponItemId, shock);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_HIT event={shape.EventId} owner={shape.OwnerId} victim={victimId} " +
            $"weapon={shape.WeaponItemId} shape=line anchor={shape.AnchorMonsterId} " +
            $"corruptionBefore={corruptionBefore} corruptionAfter={corruptionAfter}");
    }

    private void ClearSwarmCrossfireState(long matchingId)
    {
        _swarmCrossfireShapes.RemoveAll(shape => shape.MatchingId == matchingId);
        foreach (var key in _swarmCrossfireVictimImmuneUntilUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireVictimImmuneUntilUtc.Remove(key);
        foreach (var key in _swarmCrossfireOwnerLastHitAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireOwnerLastHitAtUtc.Remove(key);
    }
}
