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
///     발사 순간 원점(오브)에서 표적(몬스터·플레이어) 방향으로 티어 사거리만큼의 직선을 잠그고, 큰 투사체가
///     원점에서 끝으로 일정 속도로 날아간다 — 선상의 첫 표적(몬스터·다른 플레이어)에 닿는 순간 거기서 폭발,
///     폭발 반경 안 몬스터 전부 PvE 피해·플레이어 전부 충격 1회 (2026-08-17 유저 결정: "폭발하는
///     시점이 피해 시점"). 끝까지 아무것도 안 닿으면 폭발 없이 소멸(유저 지시). 발사 뒤 표적이 죽거나
///     움직여도 모양은 잠근 직선을 끝까지 쓴다. 예고선·예고 시간은 퇴역. 태양만 — 바람은 회전 칼날(SwarmWindSlash), 파도는 물폭탄.
/// </summary>
public partial class GameServer
{
    private static readonly bool SwarmCrossfireEnabled = true;

    // 아이소 바닥면 정규화 계수 — 접촉 판정·물폭탄 반경과 같은 dy×2.
    private const float SwarmGroundYScale = 2f;
    // 몬스터 몸통 여유 — 앞머리가 몸 가장자리를 스쳐도 맞는다 (접촉 반경 0.32와 같은 급).
    private const float SwarmCrossfireMonsterRadius = 0.3f;
    // 플레이어 몸통 여유 — 중심점만 재면 캡슐 가장자리가 몸을 스치는 장면에서 "지나갔는데 안 맞는다"
    // (2026-08-17 유저 제보). 몸 폭의 절반쯤.
    private const float SwarmCrossfirePlayerRadius = 0.25f;

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
        // 폭발 반경(바닥면) — 첫 표적에 닿아 터지는 순간의 판정 원.
        public float BlastRadius { get; init; }
        // 앞머리 속도 (바닥면 단위/초).
        public float SweepSpeed { get; init; }
        public DateTime ArmedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public int AnchorMonsterId { get; init; }
        // 기준 몬스터의 전투 표적 id — 리졸버 후보(PlayerId 자리)와 같은 값. 표적 분산 필터가 비교한다.
        public long AnchorCombatTargetId { get; init; }
        // 앞머리가 지난 축 위치(바닥면 단위, 원점 = 0). 캡 반폭 앞에서 시작한다.
        public float LastFront { get; set; }
        public HashSet<long> HitVictims { get; } = new();
    }

    private readonly List<SwarmCrossfireShape> _swarmCrossfireShapes = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireVictimImmuneUntilUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCrossfireOwnerLastHitAtUtc = new();
    private long _swarmCrossfireEventSeq;

    /// <summary>이 발사가 교차사격 모양(태양 폭발 투사체)으로 처리되는가 — 유도탄 경로를 대체한다.</summary>
    private static bool IsSwarmCrossfireWeapon(int weaponItemId) => IsSwarmCrossfireSun(weaponItemId);

    /// <summary>태양: 첫 표적에서 폭발하는 큰 투사체.</summary>
    private static bool IsSwarmCrossfireSun(int weaponItemId) =>
        SwarmCrossfireEnabled &&
        SurvivorOrbData.TryGetColorAndTier(weaponItemId, out var color, out _) &&
        color == SurvivorOrbColor.Red;

    /// <summary>
    ///     소유자가 지금 예고(시전) 중인 모양 수 — 발동 뒤 쓸고 있는 모양은 세지 않는다.
    ///     리졸버 필터(상한이면 태양이 표적을 잡지 않음)와 예약 가드가 같은 수를 본다.
    /// </summary>
    private int CountSwarmCrossfireTelegraphing(long matchingId, long ownerId, DateTime nowUtc)
    {
        int count = 0;
        foreach (var shape in _swarmCrossfireShapes)
        {
            if (shape.MatchingId == matchingId && shape.OwnerId == ownerId && nowUtc < shape.ArmedAtUtc)
                count++;
        }

        return count;
    }

    /// <summary>
    ///     표적 분산 (#232, 2026-08-17 유저 지시 "한번에 같은 걸 겨냥하지 말 것"): 소유자의 살아 있는
    ///     모양이 이미 기준으로 잡은 몬스터 쌍. 리졸버 필터가 같은 소유자의 다른 태양 오브에게 이 몹을
    ///     후보에서 빼 준다 — 다음으로 가까운 몹을 고르므로 오브마다 다른 자리를 겨눈다.
    ///     모양이 쓸고 끝나면(제거) 다시 후보가 된다. 예약(PendingDamage) 대신 이 필터를 쓰는 이유:
    ///     쓸기가 빗나가도 풀어 줄 게 없다 — 모양의 수명이 곧 배제 기간이다.
    /// </summary>
    private HashSet<(long OwnerId, long CombatTargetId)> CollectSwarmCrossfireAnchoredTargets(long matchingId)
    {
        var anchored = new HashSet<(long, long)>();
        foreach (var shape in _swarmCrossfireShapes)
        {
            if (shape.MatchingId == matchingId)
                anchored.Add((shape.OwnerId, shape.AnchorCombatTargetId));
        }

        return anchored;
    }

    /// <summary>
    ///     이번 틱에 예고 상한에 닿은 소유자들 — 리졸버 필터가 이들의 태양 오브 조준을 유예한다.
    ///     틱마다 한 번 만든다 (필터는 공격자×표적 쌍마다 불린다).
    /// </summary>
    private HashSet<long> CollectSwarmCrossfireCappedOwners(long matchingId, DateTime nowUtc)
    {
        var telegraphingByOwner = new Dictionary<long, int>();
        foreach (var shape in _swarmCrossfireShapes)
        {
            if (shape.MatchingId != matchingId || nowUtc >= shape.ArmedAtUtc)
                continue;
            telegraphingByOwner[shape.OwnerId] = telegraphingByOwner.GetValueOrDefault(shape.OwnerId) + 1;
        }

        var capped = new HashSet<long>();
        foreach (var (ownerId, count) in telegraphingByOwner)
        {
            if (count >= Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
                capped.Add(ownerId);
        }

        return capped;
    }

    /// <summary>
    ///     발사 순간 직선을 잠근다. 소유자당 동시 예고 상한에 닿아 있으면 false — 호출부는 그 발을
    ///     버린다(모양 없는 피해는 없다). 보통은 리졸버 필터가 먼저 막아 여기까지 안 온다 — 같은 틱에
    ///     여러 오브가 함께 준비된 경우만 걸린다.
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

        if (CountSwarmCrossfireTelegraphing(matchingId, attack.AttackerPlayerId, nowUtc) >=
            Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
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
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        float blastRadius = Config.SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER[tierIndex];
        float sweepSpeed = Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;
        // 길이는 표적 거리와 무관한 티어 사거리 — 강화될수록 멀리 간다. 표적은 방향만 준다.
        float groundLength = Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[tierIndex];
        var end = new Vector3f(
            origin.X + gx / anchorDistance * groundLength,
            origin.Y + gy / anchorDistance * groundLength / SwarmGroundYScale,
            0f);

        // 앞머리는 원점 앞 캡(반폭)에서 출발해 끝 너머 캡까지 간다 — 캡슐 전체를 한 번 쓴다.
        float halfWidth = width * 0.5f;
        float sweepSeconds = (groundLength + width) / sweepSpeed;
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
            BlastRadius = blastRadius,
            SweepSpeed = sweepSpeed,
            ArmedAtUtc = armedAt,
            ExpiresAtUtc = armedAt.AddSeconds(sweepSeconds),
            AnchorMonsterId = anchorMonsterId,
            AnchorCombatTargetId = attack.TargetPlayerId,
            LastFront = -halfWidth
        });
        PublishSwarmCrossfireDodgeSnapshot();

        BroadcastSwarmCrossfireTelegraph(
            eventId, attack, origin, end, width, sweepSeconds, anchorMonsterId, allSessions);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_TELEGRAPH event={eventId} owner={attack.AttackerPlayerId} " +
            $"ordinal={attack.AttackerTrailOrdinal} " +
            $"weapon={attack.WeaponItemId} tier={tier} shape=line anchor={anchorMonsterId} " +
            $"area={attack.Area} origin=({origin.X:F2},{origin.Y:F2}) end=({end.X:F2},{end.Y:F2}) " +
            $"width={width:F2} blast={blastRadius:F2} groundLength={groundLength:F2} damage={damage} " +
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
    ///     예고가 끝난 모양의 투사체 앞머리를 전진시키며, 이번 틱에 앞머리가 지난 축 구간
    ///     [지난 앞머리, 현재 앞머리] × 반폭 안에 처음 들어오는 표적(몬스터·소유자 아닌 플레이어)을 찾는다.
    ///     있으면 그 자리에서 폭발 — 반경 안 몬스터 전부 PvE 피해, 플레이어 전부 충격(면역·상한 그대로).
    ///     없이 끝까지 가면 폭발 없이 소멸. 폭발한 모양은 즉시 끝난다.
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
        int shapesBefore = _swarmCrossfireShapes.Count;
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
                  (float)(nowUtc - shape.ArmedAtUtc).TotalSeconds * shape.SweepSpeed;
            front = MathF.Min(front, sweepEnd);
            float lastFront = shape.LastFront;
            shape.LastFront = front;
            monsters ??= _swarmArenaManager.GetCombatTargets(matchingId);

            // 이번 틱 구간에서 가장 먼저(원점에 가까운 축 위치) 닿는 표적을 고른다.
            float? triggerAlong = null;
            foreach (var monster in monsters)
            {
                if (monster.Area != shape.Area)
                    continue;
                if (!TryGetSwarmCrossfireSweptAlong(
                        shape, monster.Position, lastFront, front, SwarmCrossfireMonsterRadius, out float along))
                    continue;
                if (triggerAlong == null || along < triggerAlong.Value)
                    triggerAlong = along;
            }

            foreach (var participant in participants)
            {
                if (participant.PlayerId == shape.OwnerId || participant.Area != shape.Area)
                    continue;
                if (!TryGetSwarmCrossfireSweptAlong(
                        shape, participant.Position, lastFront, front, SwarmCrossfirePlayerRadius, out float along))
                    continue;
                if (triggerAlong == null || along < triggerAlong.Value)
                    triggerAlong = along;
            }

            if (triggerAlong == null)
            {
                // 사거리 끝까지 아무것도 못 맞혔다 — 폭발 없이 소멸 (2026-08-17 유저 지시). 클라 투사체도
                // 같은 시간에 끝에 닿아 스스로 사라지므로 통지는 없다.
                if (front >= sweepEnd)
                {
                    _swarmCrossfireShapes.RemoveAt(index);
                    _gameEventLogManager.LogSystem(
                        matchingId, $"ORB_CROSSFIRE_VANISH event={shape.EventId} owner={shape.OwnerId}");
                }
                continue;
            }

            // 폭발 지점: 표적에 닿은 축 위치(선분 안으로 클램프).
            float detonateAlong = Math.Clamp(triggerAlong.Value, 0f, shape.GroundLength);
            var detonation = ResolveSwarmCrossfireAxisPoint(shape, detonateAlong);
            _swarmCrossfireShapes.RemoveAt(index);
            DetonateSwarmCrossfire(
                matchingId, shape, detonation, nowUtc,
                monsters, participants, aliveSessions, aliveBots, allSessions);
        }

        // 봇 회피 스냅샷 — 이번 틱에 소멸·폭발로 줄었으면 다시 발행한다.
        if (_swarmCrossfireShapes.Count != shapesBefore)
            PublishSwarmCrossfireDodgeSnapshot();
    }

    /// <summary>
    ///     폭발 판정·연출: 반경 안(바닥면 거리) 몬스터 전부 PvE 피해(각각 치명 굴림), 소유자 아닌 플레이어
    ///     전부 충격(피해자 면역·소유자 초당 1회 상한은 그대로). 같은 구역 전원에게 폭발 지점·반경을 보낸다 —
    ///     클라는 날아가던 투사체를 그 자리에서 터뜨린다 (표시 = 판정).
    /// </summary>
    private void DetonateSwarmCrossfire(
        long matchingId,
        SwarmCrossfireShape shape,
        Vector3f detonation,
        DateTime nowUtc,
        IReadOnlyList<SwarmArenaCombatTarget> monsters,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        BroadcastSwarmCrossfireDetonation(shape, detonation, allSessions);

        int monsterHits = 0;
        foreach (var monster in monsters)
        {
            if (monster.Area != shape.Area)
                continue;
            if (!IsWithinSwarmGroundRadius(detonation, monster.Position, shape.BlastRadius + SwarmCrossfireMonsterRadius))
                continue;

            monsterHits++;
            _swarmArenaManager.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
            int monsterDamage = RollSwarmCriticalDamage(shape.Damage, out bool critical);
            ApplySwarmMonsterHitNow(
                matchingId, monster.CombatTargetId, monster.MonsterId, shape.OwnerId,
                shape.WeaponItemId, shape.Area, monsterDamage, critical, allSessions);
        }

        int shocks = 0;
        foreach (var participant in participants)
        {
            if (participant.PlayerId == shape.OwnerId || participant.Area != shape.Area)
                continue;
            if (shape.HitVictims.Contains(participant.PlayerId))
                continue;
            if (!IsWithinSwarmGroundRadius(detonation, participant.Position, shape.BlastRadius + SwarmCrossfirePlayerRadius))
                continue;
            if (!TryClaimSwarmShockWindow(matchingId, shape.OwnerId, participant.PlayerId, nowUtc))
                continue;

            shocks++;
            shape.HitVictims.Add(participant.PlayerId);
            ApplySwarmShock(matchingId, shape.OwnerId, shape.WeaponItemId, shape.Area, participant.PlayerId,
                $"ORB_CROSSFIRE_HIT event={shape.EventId} shape=line anchor={shape.AnchorMonsterId}",
                aliveSessions, aliveBots, allSessions);
        }

        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_DETONATE event={shape.EventId} owner={shape.OwnerId} " +
            $"at=({detonation.X:F2},{detonation.Y:F2}) radius={shape.BlastRadius:F2} " +
            $"monsters={monsterHits} shocks={shocks}");
    }

    private static void BroadcastSwarmCrossfireDetonation(
        SwarmCrossfireShape shape, Vector3f detonation, List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_CROSSFIRE_TELEGRAPH);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_CROSSFIRE_TELEGRAPH
        {
            EventId = shape.EventId,
            OwnerPlayerId = shape.OwnerId,
            WeaponItemId = shape.WeaponItemId,
            Shape = Config.SWARM_CROSSFIRE_SHAPE_DETONATE,
            OriginX = detonation.X,
            OriginY = detonation.Y,
            EndX = detonation.X,
            EndY = detonation.Y,
            Width = shape.BlastRadius,
            TelegraphSeconds = 0f,
            ActiveSeconds = 0f,
            AnchorMonsterId = shape.AnchorMonsterId,
            OwnerOrbOrdinal = 0
        }));
        foreach (var session in allSessions)
        {
            if (session.PlayerId.HasValue && !session.IsEliminated && session.CurrentArea == shape.Area)
                session.Send(packet);
        }
    }

    /// <summary>축 위 위치(바닥면 거리)를 월드 좌표로 — 폭발 지점.</summary>
    private static Vector3f ResolveSwarmCrossfireAxisPoint(SwarmCrossfireShape shape, float along)
    {
        float ax = shape.Origin.X, ay = shape.Origin.Y * SwarmGroundYScale;
        float bx = shape.End.X, by = shape.End.Y * SwarmGroundYScale;
        float length = MathF.Max(shape.GroundLength, 1e-4f);
        float ux = (bx - ax) / length, uy = (by - ay) / length;
        return new Vector3f(ax + ux * along, (ay + uy * along) / SwarmGroundYScale, 0f);
    }

    /// <summary>바닥면(dy×2) 거리로 반경 안인가 — 물폭탄·접촉과 같은 공간.</summary>
    private static bool IsWithinSwarmGroundRadius(Vector3f center, Vector3f point, float radius)
    {
        float dx = point.X - center.X;
        float dy = (point.Y - center.Y) * SwarmGroundYScale;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>
    ///     쓸기 판정 (바닥면): 점을 축에 사영한 위치가 이번 틱 앞머리 구간 안이고, 축에서의 수직 거리가
    ///     반폭(+여유) 이하이면 그 축 위치를 돌려준다. 캡슐 양 끝은 캡 반폭만큼 축 구간을 늘려 판정한다.
    /// </summary>
    private static bool TryGetSwarmCrossfireSweptAlong(
        SwarmCrossfireShape shape, Vector3f point, float fromFront, float toFront, float radiusPadding,
        out float along)
    {
        along = 0f;
        float ax = shape.Origin.X, ay = shape.Origin.Y * SwarmGroundYScale;
        float bx = shape.End.X, by = shape.End.Y * SwarmGroundYScale;
        float px = point.X, py = point.Y * SwarmGroundYScale;
        float abx = bx - ax, aby = by - ay;
        float length = shape.GroundLength;
        if (length <= 0f)
            return false;
        float ux = abx / length, uy = aby / length;
        along = (px - ax) * ux + (py - ay) * uy;
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
    ///     즉시 몬스터 타격 (교차사격 쓸기). 착탄 지연 큐와 같은 정산 —
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

    /// <summary>
    ///     충격 창 (명세 공통 규칙): 피해자는 공격자와 무관하게 0.9초 면역, 소유자는 다른 플레이어에게 초당 1회.
    ///     통과하면 두 창을 이 시각으로 갱신하고 true — 교차사격 폭발·바람 회전 칼날이 같은 창을 쓴다.
    /// </summary>
    private bool TryClaimSwarmShockWindow(long matchingId, long ownerId, long victimId, DateTime nowUtc)
    {
        if (_swarmCrossfireVictimImmuneUntilUtc.TryGetValue((matchingId, victimId), out var immuneUntil) &&
            nowUtc < immuneUntil)
            return false;
        if (_swarmCrossfireOwnerLastHitAtUtc.TryGetValue((matchingId, ownerId), out var ownerLastHit) &&
            (nowUtc - ownerLastHit).TotalSeconds < Config.SWARM_CROSSFIRE_OWNER_HIT_INTERVAL_SECONDS)
            return false;

        _swarmCrossfireVictimImmuneUntilUtc[(matchingId, victimId)] =
            nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_VICTIM_IMMUNE_SECONDS);
        _swarmCrossfireOwnerLastHitAtUtc[(matchingId, ownerId)] = nowUtc;
        return true;
    }

    /// <summary>
    ///     플레이어 충격 (고정값, 티어 무관): 사격 피격 경로를 재사용해 오염 증가·피격 숫자·탈락 흐름이 그대로
    ///     따라온다. 봇도 같은 값. 소유자 화면에는 사격 피드백을 보낸다. label은 로그용(어느 모양이 때렸나).
    /// </summary>
    private void ApplySwarmShock(
        long matchingId,
        long ownerId,
        int weaponItemId,
        AreaType area,
        long victimId,
        string label,
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
            victimSession.ApplyProximityAutoCombatHit(ownerId, area, weaponItemId, shock);
            corruptionAfter = victimSession.CurrentCorruption;
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == victimId);
            if (bot == null)
                return;
            corruptionBefore = bot.Corruption;
            bot.LastProximityAttackerPlayerId = ownerId;
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            _gameEventLogManager.LogSurvivorHit(
                matchingId, ownerId, bot.PlayerId, weaponItemId, shock,
                bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                bot.Corruption + shock >= Config.SURVIVOR_MAX_CORRUPTION,
                BotPlayerManager.IsBotPlayerId(ownerId), DateTimeOffset.UtcNow);
            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + shock);
            corruptionAfter = bot.Corruption;
        }

        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == ownerId);
        ownerSession?.SendProximityAutoCombatAttackFeedback(victimId, area, weaponItemId, shock);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"{label} owner={ownerId} victim={victimId} weapon={weaponItemId} " +
            $"corruptionBefore={corruptionBefore} corruptionAfter={corruptionAfter}");
    }

    private void ClearSwarmCrossfireState(long matchingId)
    {
        _swarmCrossfireShapes.RemoveAll(shape => shape.MatchingId == matchingId);
        PublishSwarmCrossfireDodgeSnapshot();
        foreach (var key in _swarmCrossfireVictimImmuneUntilUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireVictimImmuneUntilUtc.Remove(key);
        foreach (var key in _swarmCrossfireOwnerLastHitAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireOwnerLastHitAtUtc.Remove(key);
    }
}
