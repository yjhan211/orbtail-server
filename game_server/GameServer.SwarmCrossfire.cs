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
///     교차사격 (#232 2단계 → 2026-08-24 뱀서식 관통). 오브는 몬스터만 조준하지만, 태양의 공격은
///     유도탄이 아니라 큰 투사체가 같은 타일 X/Y축을 공유하는 표적 방향으로 직진하는 공격이다.
///     길이는 항상 구역 경계(벽)까지다 — 티어 사거리는 표적 획득에만 쓰고, 구역 안 프랍은 통과.
///     판정은 관통 (2026-08-24 유저 결정): 선상의 몬스터·플레이어는 앞머리가 지나가는 순간 각각
///     피해를 입고(한 발에 한 번), 투사체는 멈추지 않는다. 벽에 막힌 직선은 피해 없는 시각 폭발로 종료한다.
///     벽 없이 사거리 끝까지 가면
///     폭발 없이 소멸. "첫 표적 폭발"(2026-08-17)은 퇴역 — 회피가 프랍·벽 배치 읽기가 되게 한다.
///     발사 뒤 표적이 죽거나 움직여도 모양은 잠근 직선을 끝까지 쓴다. 태양만 — 바람은 회전 칼날(SwarmWindSlash), 파도는 물폭탄.
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
        // 벽 충돌 시각 폭발의 표시 반경(바닥면). 추가 피해 판정에는 사용하지 않는다.
        public float BlastRadius { get; init; }
        // 앞머리 속도 (바닥면 단위/초).
        public float SweepSpeed { get; init; }
        public DateTime ArmedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        // 벽 폭발 (2026-08-24 관통): 직선이 벽에 막혀 잘렸으면 끝점에서 터진다 — 사거리 소진이면 소멸.
        public bool DetonateAtWall { get; init; }
        public int AnchorMonsterId { get; init; }
        // 기준 몬스터의 전투 표적 id — 리졸버 후보(PlayerId 자리)와 같은 값. 표적 분산 필터가 비교한다.
        public long AnchorCombatTargetId { get; init; }
        // 앞머리가 지난 축 위치(바닥면 단위, 원점 = 0). 캡 반폭 앞에서 시작한다.
        public float LastFront { get; set; }
        public HashSet<long> HitVictims { get; } = new();
        // 관통으로 이미 맞은 몬스터(CombatTargetId) — 한 발에 한 번.
        public HashSet<long> HitMonsters { get; } = new();
    }

    private readonly List<SwarmCrossfireShape> _swarmCrossfireShapes = new();
    private long _swarmCrossfireEventSeq;

    /// <summary>이 발사가 교차사격 모양(태양 폭발 투사체)으로 처리되는가 — 유도탄 경로를 대체한다.</summary>
    private static bool IsSwarmCrossfireWeapon(int weaponItemId) => IsSwarmCrossfireSun(weaponItemId);

    /// <summary>태양: 첫 표적에서 폭발하는 큰 투사체.</summary>
    private static bool IsSwarmCrossfireSun(int weaponItemId) =>
        SwarmCrossfireEnabled &&
        OrbData.TryGetColorAndTier(weaponItemId, out var color, out _) &&
        color == OrbColor.Red;

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
        OrbData.TryGetColorAndTier(attack.WeaponItemId, out _, out int tier);

        if (CountSwarmCrossfireTelegraphing(matchingId, attack.AttackerPlayerId, nowUtc) >=
            Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
            return false;

        // 조준 개편 (2026-08-26 유저 결정 "가로/세로 축 나누기 폐지"): 꼬리 접선 기반 축
        // 잠금(2026-08-24 국소 창 가중 접선)은 퇴역 — 오브 위치·꼬리 모양과 무관하게 타일
        // 4방향(가로 ±, 세로 ±) 후보 선을 전부 벽까지 만들어, 실제 선분 위에 표적이 있는
        // 방향을 우선 조준한다 (몹 수 → 첫 적중 거리). 어느 선에도 표적이 없으면 발사 사유였던
        // 기준 표적 쪽 사영이 가장 큰 방향으로 쏜다. 유효한 방향(벽 여유 0.3)이 없으면 생략
        // (환불 — 벽에 붙은 태양 공이 제자리 폭발하는 것보다 자연스럽다).
        float groundDx = anchor.X - origin.X;
        float groundDy = (anchor.Y - origin.Y) * SwarmGroundYScale;
        if (groundDx * groundDx + groundDy * groundDy < 0.0025f)
            return false;

        int tierIndex = Math.Clamp(tier, 1, 3) - 1;
        float width = Config.SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER[tierIndex];
        float halfWidth = width * 0.5f;
        float blastRadius = Config.SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER[tierIndex];
        float sweepSpeed = Config.SWARM_CROSSFIRE_SUN_SWEEP_SPEED;

        const float diagonalUnit = 0.70710677f;
        // 타일 X축 = 바닥면 (+1,+1)/√2, 타일 Y축 = (-1,+1)/√2 — 부호까지 4방향.
        ReadOnlySpan<float> directionX = stackalloc float[]
            { diagonalUnit, -diagonalUnit, -diagonalUnit, diagonalUnit };
        ReadOnlySpan<float> directionY = stackalloc float[]
            { diagonalUnit, -diagonalUnit, diagonalUnit, -diagonalUnit };

        var combatTargets = _swarmArenaManager.GetCombatTargets(matchingId);
        float unitX = 0f;
        float unitY = 0f;
        float groundLength = 0f;
        int bestHits = 0;
        float bestNearest = float.MaxValue;
        float fallbackProjection = float.MinValue;
        bool resolved = false;
        bool resolvedByHits = false;
        for (int direction = 0; direction < 4; direction++)
        {
            float candidateLength = FindSwarmCrossfireWallDistance(
                origin, directionX[direction], directionY[direction],
                SwarmCrossfireMaxGroundLength, attack.Area);
            if (candidateLength < 0.3f)
                continue;

            CountSwarmCrossfireLineTargets(
                combatTargets, attack.Area, origin, directionX[direction], directionY[direction],
                candidateLength, halfWidth, out int hits, out float nearest);
            // 거리 우선 (2026-08-26 2차: 몹 수 → 거리에서 뒤집음): 몹 수 우선은 긴 축이 항상
            // 이겨 좁은 복도의 세로 발사가 죽었다 — 코앞 표적 쪽으로 응사하고, 같은 거리면 많은 쪽.
            bool better = hits > 0 &&
                          (!resolvedByHits ||
                           nearest < bestNearest - 0.001f ||
                           (MathF.Abs(nearest - bestNearest) <= 0.001f && hits > bestHits));
            if (better)
            {
                bestHits = hits;
                bestNearest = nearest;
                unitX = directionX[direction];
                unitY = directionY[direction];
                groundLength = candidateLength;
                resolved = true;
                resolvedByHits = true;
            }

            if (resolvedByHits)
                continue;
            float projection = groundDx * directionX[direction] + groundDy * directionY[direction];
            if (projection > fallbackProjection)
            {
                fallbackProjection = projection;
                unitX = directionX[direction];
                unitY = directionY[direction];
                groundLength = candidateLength;
                resolved = true;
            }
        }

        if (!resolved)
            return false;

        bool detonateAtWall = groundLength < SwarmCrossfireMaxGroundLength - 0.01f;
        var end = new Vector3f(
            origin.X + unitX * groundLength,
            origin.Y + unitY * groundLength / SwarmGroundYScale,
            0f);

        // 앞머리는 원점 앞 캡(반폭)에서 출발해 끝 너머 캡까지 간다 — 캡슐 전체를 한 번 쓴다.
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
            DetonateAtWall = detonateAtWall,
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
    ///     [지난 앞머리, 현재 앞머리] × 반폭 안의 표적을 모두 관통 타격한다(대상당 한 번).
    ///     벽에 닿으면 피해 없는 시각 폭발로, 벽이 없으면 폭발 없이 소멸한다.
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

            // 관통 (2026-08-24 유저 결정, 뱀서식): 이번 틱 구간에 걸린 표적 전부를 지나가며 때린다 —
            // 첫 표적 폭발은 퇴역. 투사체는 멈추지 않고, 폭발은 벽에 닿을 때만.
            foreach (var monster in monsters)
            {
                if (monster.Area != shape.Area || shape.HitMonsters.Contains(monster.CombatTargetId))
                    continue;
                if (!TryGetSwarmCrossfireSweptMonsterBody(
                        shape, monster.Position, lastFront, front, out _))
                    continue;

                shape.HitMonsters.Add(monster.CombatTargetId);
                TrackSwarmCrossfireConvergence(matchingId, monster.CombatTargetId, nowUtc);
                _swarmArenaManager.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                int monsterDamage = RollSwarmCriticalDamage(shape.Damage, out bool critical);
                ApplySwarmMonsterHitNow(
                    matchingId, monster.CombatTargetId, monster.MonsterId, shape.OwnerId,
                    shape.WeaponItemId, shape.Area, monsterDamage, critical, allSessions);
            }

            foreach (var participant in participants)
            {
                if (participant.PlayerId == shape.OwnerId || participant.Area != shape.Area ||
                    shape.HitVictims.Contains(participant.PlayerId))
                    continue;
                if (!TryGetSwarmCrossfireSweptPlayerBody(
                        shape, participant.Position, lastFront, front, out _))
                    continue;

                shape.HitVictims.Add(participant.PlayerId);
                ApplySwarmShock(
                    matchingId, shape.OwnerId, shape.WeaponItemId, shape.Area, participant.PlayerId,
                    $"ORB_CROSSFIRE_HIT event={shape.EventId} shape=pierce anchor={shape.AnchorMonsterId}",
                    aliveSessions, aliveBots, allSessions);
                ApplySwarmSunBurn(
                    matchingId, shape.OwnerId, shape.WeaponItemId, shape.Area,
                    participant.PlayerId, nowUtc, aliveSessions);
            }

            if (front < sweepEnd)
                continue;

            _swarmCrossfireShapes.RemoveAt(index);
            if (shape.DetonateAtWall)
            {
                // 벽 폭발: 잘린 직선의 끝(= 첫 이동 불가 셀 앞)에서 터진다.
                var detonation = ResolveSwarmCrossfireAxisPoint(shape, shape.GroundLength);
                DetonateSwarmCrossfire(matchingId, shape, detonation, allSessions);
            }
            else
            {
                // 벽 없이 사거리 소진 — 폭발 없이 소멸. 클라 투사체도 같은 시간에 끝에 닿아
                // 스스로 사라지므로 통지는 없다.
                _gameEventLogManager.LogSystem(
                    matchingId, $"ORB_CROSSFIRE_VANISH event={shape.EventId} owner={shape.OwnerId}");
            }
        }

        // 봇 회피 스냅샷 — 이번 틱에 소멸·폭발로 줄었으면 다시 발행한다.
        if (_swarmCrossfireShapes.Count != shapesBefore)
            PublishSwarmCrossfireDodgeSnapshot();
    }

    /// <summary>
    ///     벽 충돌 시각 연출. 같은 구역 전원에게 폭발 지점·표시 반경만 보내며 추가 피해나 충격은 없다.
    /// </summary>
    private void DetonateSwarmCrossfire(
        long matchingId,
        SwarmCrossfireShape shape,
        Vector3f detonation,
        List<GameClientSession> allSessions)
    {
        BroadcastSwarmCrossfireDetonation(shape, detonation, allSessions);
        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_DETONATE event={shape.EventId} owner={shape.OwnerId} " +
            $"at=({detonation.X:F2},{detonation.Y:F2}) radius={shape.BlastRadius:F2} visualOnly=true");
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

    // 벽 탐색 표본 간격(바닥면 단위) — 셀(1) 대비 충분히 촘촘하다.
    private const float SwarmCrossfireWallProbeStep = 0.2f;
    // 벽 탐색 상한(바닥면 단위) — 가장 큰 구역 대각보다 넉넉히 크다. 여기까지 경계를 못 찾으면
    // (구역 데이터 이상) 폭발 없이 소멸하는 안전망으로 떨어진다.
    private const float SwarmCrossfireMaxGroundLength = 40f;

    /// <summary>
    ///     원점에서 바닥면 단위 방향(unitX, unitY)으로 표본을 전진시키며 첫 구역 밖 셀(구역 경계·벽)까지의
    ///     거리를 찾는다 — 없으면 maxLength. 이동 불가 셀이 아니라 구역 소속을 보는 이유 (2026-08-24 유저
    ///     결정): 골대 같은 구역 안 프랍(이동 불가 셀)에서 멈추면 회피가 아니라 운으로 읽혀서, 프랍은
    ///     관통하고 진짜 벽에서만 터진다. 전투 판정이 전부 구역 단위라 구역 경계 = 판정 공간의 끝이다.
    /// </summary>
    private static float FindSwarmCrossfireWallDistance(
        Vector3f origin, float unitX, float unitY, float maxLength, AreaType area)
    {
        for (float along = SwarmCrossfireWallProbeStep; along < maxLength;
             along += SwarmCrossfireWallProbeStep)
        {
            var probe = new Vector3f(
                origin.X + unitX * along,
                origin.Y + unitY * along / SwarmGroundYScale,
                0f);
            var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, probe);
            if (GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) != area)
                return along;
        }

        return maxLength;
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

    // 플레이어 몸통 캡슐 (2026-08-26 유저 결정 "큰 캡슐", 같은 날 화면 정합 보정): 판정
    // 기준점(발 피벗) 하나로는 투사체가 화면에서 몸을 겹쳐 지나가도 미스였다 — 지면 타원(dy×2)이
    // 세로 화면 거리를 반으로 치기 때문. 표본 범위는 "화면에서 공이 몸(발~머리 0.9)과 겹치는"
    // 차선 범위다: 클라가 선·공을 부양된 오브 높이(+SWARM_ORB_ORBIT_CENTER_OFFSET_Y)에 그리므로,
    // 지면 판정 차선 기준으로는 발 아래 -부양 ~ 머리 +0.1이 그 범위가 된다 (표시 = 판정).
    private const float SwarmCrossfirePlayerBodyHeight = 0.9f;

    private static bool TryGetSwarmCrossfireSweptPlayerBody(
        SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront, out float along)
    {
        return TryGetSwarmCrossfireSweptBody(
            shape, position, fromFront, toFront,
            SwarmCrossfirePlayerRadius, SwarmCrossfirePlayerBodyHeight, out along);
    }

    // 몹 몸통 (2026-08-26 유저 제보 "몹은 충돌하면 안 맞고 비껴가면 맞는다"): 표시 차선이
    // 부양(+0.8) 높이라 지면 판정 그대로면 화면 겹침과 판정이 반전된다 — 플레이어와 같은
    // 화면 정합 표본을 쓴다. 몸 높이는 소형 몹 기준 0.6.
    private const float SwarmCrossfireMonsterBodyHeight = 0.6f;

    private static bool TryGetSwarmCrossfireSweptMonsterBody(
        SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront, out float along)
    {
        return TryGetSwarmCrossfireSweptBody(
            shape, position, fromFront, toFront,
            SwarmCrossfireMonsterRadius, SwarmCrossfireMonsterBodyHeight, out along);
    }

    /// <summary>
    ///     화면 정합 몸통 표본: 클라가 선·공을 부양 높이(+SWARM_ORB_ORBIT_CENTER_OFFSET_Y)에
    ///     그리므로, 지면 판정 차선 기준으로 "화면에서 공이 몸(발~bodyHeight)과 겹치는" 범위는
    ///     발 아래 -부양 ~ (bodyHeight - 부양)이다. 그 구간을 0.45 간격(지면 0.9 — 도달 반경보다
    ///     촘촘)으로 표본해 하나라도 걸리면 명중.
    /// </summary>
    private static bool TryGetSwarmCrossfireSweptBody(
        SwarmCrossfireShape shape, Vector3f position, float fromFront, float toFront,
        float radiusPadding, float bodyHeight, out float along)
    {
        float bodyStart = -Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        float bodyEnd = bodyHeight - Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        for (float bodyY = bodyStart; bodyY <= bodyEnd + 0.001f; bodyY += 0.45f)
        {
            var sample = new Vector3f(position.X, position.Y + bodyY, 0f);
            if (TryGetSwarmCrossfireSweptAlong(
                    shape, sample, fromFront, toFront, radiusPadding, out along))
                return true;
        }

        along = 0f;
        return false;
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

        _gameEventLogManager.RecordMonsterHit(matchingId, attackerId, damage, damageResult.Killed);
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
            damageResult.HeartReward, damageResult.BootsReward, damageResult.KeyReward,
            killerPlayerId: attackerId,
            isCore: damageResult.Kind == SwarmMonsterKind.RunawayGoblin);
    }

    // 충격 면역 퇴역 이력: 소유자 초당 1회 상한(2026-08-24)에 이어 피해자 0.9초 면역도
    // 퇴역(2026-08-26 유저 결정 "충격 면역 아예 없애자") — 태양 다발 화망에서 첫 발 이후가
    // 소리 없이 관통해 "피격박스가 안 맞는" 오독을 만들었다. 표시 = 판정: 지나간 발은 다 맞는다.
    // 한 발이 같은 사람을 두 번 치는 것은 발 단위 HitVictims(교차사격)·틱 주기(칼날)가 막는다.

    // 화상 (#268, 2026-08-25 유저 결정): 태양 충격 피격자에게 3초 틱 피해. 재피격은 지속 갱신.
    private readonly Dictionary<(long MatchingId, long VictimId),
        (long OwnerId, int WeaponItemId, AreaType Area, DateTime UntilUtc, DateTime NextTickAtUtc)>
        _swarmSunBurns = new();

    /// <summary>화상 부여·갱신 — 첫 틱은 1초 뒤(직격과 같은 프레임에 겹치지 않게). HUD 통지 포함.</summary>
    private void ApplySwarmSunBurn(
        long matchingId, long ownerId, int weaponItemId, AreaType area, long victimId,
        DateTime nowUtc, List<GameClientSession> aliveSessions)
    {
        _swarmSunBurns[(matchingId, victimId)] = (
            ownerId, weaponItemId, area,
            nowUtc.AddSeconds(Config.SWARM_SUN_BURN_SECONDS),
            nowUtc.AddSeconds(Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS));
        aliveSessions.FirstOrDefault(session => session.PlayerId == victimId)
            ?.SendSwarmSunBurn(ownerId, area, (int)(Config.SWARM_SUN_BURN_SECONDS * 1000f));
    }

    /// <summary>화상 틱 정산 — 초당 한 번, 충격의 0.2배. 지속이 끝나면 걷는다.</summary>
    private void ProcessSwarmSunBurns(
        long matchingId,
        DateTime nowUtc,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        List<(long, long)> expired = null;
        foreach (var pair in _swarmSunBurns)
        {
            if (pair.Key.MatchingId != matchingId)
                continue;
            var burn = pair.Value;
            if (nowUtc >= burn.NextTickAtUtc)
            {
                ApplySwarmShock(matchingId, burn.OwnerId, burn.WeaponItemId, burn.Area,
                    pair.Key.VictimId, "SUN_BURN_TICK", aliveSessions, aliveBots, allSessions,
                    Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER, dotTick: true);
                _swarmSunBurns[pair.Key] = burn with
                {
                    NextTickAtUtc = burn.NextTickAtUtc.AddSeconds(
                        Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS)
                };
            }

            if (nowUtc >= burn.UntilUtc)
                (expired ??= new List<(long, long)>()).Add(pair.Key);
        }

        if (expired == null)
            return;
        foreach (var key in expired)
            _swarmSunBurns.Remove(key);
    }

    private void ClearSwarmSunBurnState(long matchingId)
    {
        foreach (var key in _swarmSunBurns.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmSunBurns.Remove(key);
    }

    // 상처 (#268, 2026-08-25 유저 결정): 바람 칼날 피격자는 5초간 PvP 충격 치명타가 열린다.
    private readonly Dictionary<(long MatchingId, long VictimId), DateTime> _swarmWindWoundsUntilUtc = new();

    /// <summary>상처 부여·갱신 — HUD 통지 포함. 효과는 ApplySwarmShock의 치명타 굴림이 읽는다.</summary>
    private void ApplySwarmWindWound(
        long matchingId, long ownerId, AreaType area, long victimId,
        DateTime nowUtc, List<GameClientSession> aliveSessions)
    {
        _swarmWindWoundsUntilUtc[(matchingId, victimId)] =
            nowUtc.AddSeconds(Config.SWARM_WIND_WOUND_SECONDS);
        aliveSessions.FirstOrDefault(session => session.PlayerId == victimId)
            ?.SendSwarmWindWound(ownerId, area, (int)(Config.SWARM_WIND_WOUND_SECONDS * 1000f));
    }

    private bool IsSwarmWounded(long matchingId, long victimId)
    {
        return _swarmWindWoundsUntilUtc.TryGetValue((matchingId, victimId), out var untilUtc) &&
               DateTime.UtcNow < untilUtc;
    }

    private void ClearSwarmWindWoundState(long matchingId)
    {
        foreach (var key in _swarmWindWoundsUntilUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmWindWoundsUntilUtc.Remove(key);
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
        List<GameClientSession> allSessions,
        float damageScale = 1f,
        bool dotTick = false)
    {
        // 받는 피해 배율 (2026-08-18): 고정 50 × 1/3 → 17. 태양·바람·파도 충격이 전부 이 한 곳을 지난다.
        // damageScale: 파도 소용돌이(#268)는 당김이 본체라 피해를 타격 피드백 수준(1/4)으로 줄인다.
        int shock = Math.Max(1, (int)MathF.Round(
            Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_CORRUPTION) * damageScale));
        // 상처 (#268): 상처 입은 피해자만 PvP 충격 치명타가 열린다 — PvE와 같은 2배.
        if (IsSwarmWounded(matchingId, victimId) &&
            _swarmCriticalRng.NextDouble() < Config.SWARM_WIND_WOUND_CRIT_CHANCE)
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * SwarmCriticalMultiplier));
        int corruptionBefore;
        int corruptionAfter;

        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        if (victimSession != null)
        {
            corruptionBefore = victimSession.CurrentCorruption;
            // 사격 피격 경로 재사용 — 오염 증가·피격 숫자·탈락 흐름이 그대로 따라온다.
            victimSession.ApplyProximityAutoCombatHit(ownerId, area, weaponItemId, shock, dotTick);
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
            _gameEventLogManager.LogHit(
                matchingId, ownerId, bot.PlayerId, weaponItemId, shock,
                bot.Corruption < Config.MAX_CORRUPTION &&
                bot.Corruption + shock >= Config.MAX_CORRUPTION,
                BotPlayerManager.IsBotPlayerId(ownerId), DateTimeOffset.UtcNow);
            bot.Corruption = Math.Min(Config.MAX_CORRUPTION, bot.Corruption + shock);
            corruptionAfter = bot.Corruption;
        }

        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == ownerId);
        ownerSession?.SendProximityAutoCombatAttackFeedback(victimId, area, weaponItemId, shock, dotTick);

        _gameEventLogManager.LogSystem(
            matchingId,
            $"{label} owner={ownerId} victim={victimId} weapon={weaponItemId} " +
            $"corruptionBefore={corruptionBefore} corruptionAfter={corruptionAfter}");
    }

    /// <summary>후보 선분(벽까지 잘린 실제 길이) 위의 몹 수와 첫 적중 거리 — 좌우 선택의 근거.</summary>
    private static void CountSwarmCrossfireLineTargets(
        IReadOnlyList<SwarmArenaCombatTarget> combatTargets,
        AreaType area,
        Vector3f origin,
        float unitX,
        float unitY,
        float groundLength,
        float halfWidth,
        out int hitCount,
        out float nearestAlong)
    {
        hitCount = 0;
        nearestAlong = float.MaxValue;
        float reach = halfWidth + SwarmCrossfireMonsterRadius;
        // 화면 정합 (2026-08-26, 같은 날 2차: 중앙 1점 → 명중과 같은 몸통 표본): 중앙 1점만
        // 세면 대각 축의 수직 성분(0.7)이 도달 반경(0.65)을 넘어, 실제로 맞을 몹이 카운트에서
        // 빠졌다 — 좁은 복도의 세로 후보가 0마리로 집계돼 선택되지 않던 원인.
        float bodyStart = -Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        float bodyEnd = SwarmCrossfireMonsterBodyHeight - Config.SWARM_ORB_ORBIT_CENTER_OFFSET_Y;
        foreach (var target in combatTargets)
        {
            if (target.Area != area)
                continue;
            float bestAlong = float.MaxValue;
            for (float bodyY = bodyStart; bodyY <= bodyEnd + 0.001f; bodyY += 0.45f)
            {
                float relX = target.Position.X - origin.X;
                float relY = (target.Position.Y + bodyY - origin.Y) * SwarmGroundYScale;
                float along = relX * unitX + relY * unitY;
                if (along < 0f || along > groundLength)
                    continue;
                if (MathF.Abs(relX * unitY - relY * unitX) > reach)
                    continue;
                bestAlong = MathF.Min(bestAlong, along);
            }

            if (bestAlong >= float.MaxValue)
                continue;
            hitCount++;
            if (bestAlong < nearestAlong)
                nearestAlong = bestAlong;
        }
    }

    // 수렴 계측 (2026-08-24): 원호·ㄱ자 꼬리에서 여러 선이 같은 표적에 겹치는 정도를 잰다 —
    // 같은 표적 2발째부터 피해 감쇠를 넣을지의 판단 근거(측정 먼저, 제한은 과할 때만).
    private readonly Dictionary<(long MatchingId, long TargetId), (DateTime WindowStartUtc, int Count)>
        _swarmCrossfireConvergeWindows = new();

    /// <summary>1초 창 안에 같은 표적이 교차사격을 두 발 이상 맞으면 crossfire_converge로 남긴다.</summary>
    private void TrackSwarmCrossfireConvergence(long matchingId, long targetId, DateTime nowUtc)
    {
        // 창이 지난 엔트리가 쌓이지 않게 이따금 걷어낸다 — 몹 id는 스폰마다 새로 나온다.
        if (_swarmCrossfireConvergeWindows.Count > 512)
        {
            foreach (var staleKey in _swarmCrossfireConvergeWindows
                         .Where(pair => (nowUtc - pair.Value.WindowStartUtc).TotalSeconds > 1d)
                         .Select(pair => pair.Key).ToList())
                _swarmCrossfireConvergeWindows.Remove(staleKey);
        }

        var key = (matchingId, targetId);
        if (!_swarmCrossfireConvergeWindows.TryGetValue(key, out var window) ||
            (nowUtc - window.WindowStartUtc).TotalSeconds > 1d)
            window = (nowUtc, 0);
        window.Count++;
        _swarmCrossfireConvergeWindows[key] = window;
        if (window.Count >= 2)
        {
            _gameEventLogManager.LogSystem(
                matchingId,
                $"crossfire_converge target={targetId} hits={window.Count} " +
                $"windowMs={(nowUtc - window.WindowStartUtc).TotalMilliseconds:F0}");
        }
    }

    private void ClearSwarmCrossfireState(long matchingId)
    {
        _swarmCrossfireShapes.RemoveAll(shape => shape.MatchingId == matchingId);
        PublishSwarmCrossfireDodgeSnapshot();
        foreach (var key in _swarmCrossfireConvergeWindows.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCrossfireConvergeWindows.Remove(key);
        ClearSwarmSunBurnState(matchingId);
    }
}
