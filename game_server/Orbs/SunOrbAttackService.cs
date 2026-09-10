using game_server.players;
using game_server.players.bots;
using game_server.logging;
using game_server.monsters;
using game_server.combat;
using game_server.orbs;
using game_server.matches;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.orbs;

/// <summary>
///     태양 오브의 직선 예고·관통 판정·벽 폭발·화상 틱을 처리한다.
///     발사체와 화상 상태는 각 매치가 소유하며 호출자는 매치 잠금을 보유한다.
///     피해는 공통 전투 서비스에 위임하고, 발사체 제거 뒤 봇 회피 스냅샷을 갱신한다.
/// </summary>
internal sealed class SunOrbAttackService(
    MatchRuntimeStore matchRuntimes,
    PlayerEliminationService playerEliminations,
    GameEventLogManager eventLogs)
{
    private static readonly bool SwarmCrossfireEnabled = true;

    // 아이소 바닥면 정규화 계수 — 접촉 판정·물폭탄 반경과 같은 dy×2.
    // 봇 회피(SwarmBotDodgePolicy)와 같은 기하를 써야 해서 정책 상수를 별칭한다 (#297).
    private const float SwarmGroundYScale = SwarmBotDodgePolicy.SwarmGroundYScale;
    // 몬스터 몸통 여유 — 앞머리가 몸 가장자리를 스쳐도 맞는다 (접촉 반경 0.32와 같은 급).
    private const float SwarmCrossfireMonsterRadius = SwarmCombatGeometry.MonsterRadius;
    // 플레이어 몸통 여유 — 중심점만 재면 캡슐 가장자리가 몸을 스치는 장면에서 "지나갔는데 안 맞는다". 몸 폭의 절반쯤.
    private const float SwarmCrossfirePlayerRadius = SwarmBotDodgePolicy.SwarmCrossfirePlayerRadius;

    /// <summary>이 발사가 교차사격 모양(태양 폭발 투사체)으로 처리되는가 — 유도탄 경로를 대체한다.</summary>
    public static bool IsSwarmCrossfireWeapon(int weaponItemId) => IsSwarmCrossfireSun(weaponItemId);

    /// <summary>태양: 첫 표적에서 폭발하는 큰 투사체.</summary>
    public static bool IsSwarmCrossfireSun(int weaponItemId) =>
        SwarmCrossfireEnabled &&
        OrbData.TryGetColorAndTier(weaponItemId, out var color, out _) &&
        color == OrbColor.Red;

    /// <summary>
    ///     소유자가 지금 예고(시전) 중인 모양 수 — 발동 뒤 쓸고 있는 모양은 세지 않는다.
    ///     리졸버 필터(상한이면 태양이 표적을 잡지 않음)와 예약 가드가 같은 수를 본다.
    /// </summary>
    private int CountSwarmCrossfireTelegraphing(long matchingId, long ownerId, DateTime nowUtc)
        => matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.CountTelegraphing(ownerId, nowUtc);

    /// <summary>
    ///     표적 분산 (#232, "한번에 같은 걸 겨냥하지 말 것"): 소유자의 살아 있는
    ///     모양이 이미 기준으로 잡은 몬스터 쌍. 리졸버 필터가 같은 소유자의 다른 태양 오브에게 이 몹을
    ///     후보에서 빼 준다 — 다음으로 가까운 몹을 고르므로 오브마다 다른 자리를 겨눈다.
    ///     모양이 쓸고 끝나면(제거) 다시 후보가 된다. 예약(PendingDamage) 대신 이 필터를 쓰는 이유:
    ///     쓸기가 빗나가도 풀어 줄 게 없다 — 모양의 수명이 곧 배제 기간이다.
    /// </summary>
    public HashSet<(long OwnerId, long CombatTargetId)> CollectSwarmCrossfireAnchoredTargets(long matchingId)
        => matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.CollectAnchoredTargets();

    /// <summary>
    ///     이번 틱에 예고 상한에 닿은 소유자들 — 리졸버 필터가 이들의 태양 오브 조준을 유예한다.
    ///     틱마다 한 번 만든다 (필터는 공격자×표적 쌍마다 불린다).
    /// </summary>
    public HashSet<long> CollectSwarmCrossfireCappedOwners(long matchingId, DateTime nowUtc)
        => matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.CollectCappedOwners(
            nowUtc,
            Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER);

    /// <summary>
    ///     발사 순간 직선을 잠근다. 소유자당 동시 예고 상한에 닿아 있으면 false — 호출부는 그 발을
    ///     버린다(모양 없는 피해는 없다). 보통은 리졸버 필터가 먼저 막아 여기까지 안 온다 — 같은 틱에
    ///     여러 오브가 함께 준비된 경우만 걸린다.
    /// </summary>
    public bool TryScheduleSwarmCrossfire(
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
        SunOrbAttackState sunOrbAttacks = matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks;

        if (CountSwarmCrossfireTelegraphing(matchingId, attack.AttackerPlayerId, nowUtc) >=
            Config.SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER)
            return false;

        // 조준: 꼬리 접선이나 오브 위치와 무관하게 타일 4방향(가로 ±, 세로 ±) 후보 선을 전부 벽까지 만들어,
        // 실제 선분 위에 표적이 있는 방향을 우선 조준한다(첫 적중 거리 우선, 다음 몹 수). 어느 선에도 표적이
        // 없으면 발사 사유였던 기준 표적 쪽 사영이 가장 큰 방향으로 쏜다. 유효한 방향(벽 여유 0.3)이 없으면 생략
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

        var combatTargets = matchRuntimes.GetOrThrow(matchingId).Monsters.GetCombatTargets(matchingId);
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
            // 거리 우선: 몹 수 우선은 긴 축이 항상 이겨 좁은 복도의 세로 발사가 죽는다 — 코앞 표적 쪽으로
            // 응사하고, 같은 거리면 많은 쪽.
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
        long eventId = sunOrbAttacks.AllocateEventId();
        var armedAt = nowUtc.AddSeconds(Config.SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS);
        sunOrbAttacks.AddShape(new SwarmCrossfireShape
        {
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

        BroadcastSwarmCrossfireTelegraph(
            eventId, attack, origin, end, width, sweepSeconds, anchorMonsterId, allSessions);

        eventLogs.LogSystem(
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
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUN_ORB_ATTACK
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
            if (session.PlayerId.HasValue && !session.Player.IsEliminated && session.Player.CurrentArea == attack.Area)
                session.TrySend(packet);
        }
    }

    /// <summary>
    ///     예고가 끝난 모양의 투사체 앞머리를 전진시키며, 이번 틱에 앞머리가 지난 축 구간
    ///     [지난 앞머리, 현재 앞머리] × 반폭 안의 표적을 모두 관통 타격한다(대상당 한 번).
    ///     벽에 닿으면 피해 없는 시각 폭발로, 벽이 없으면 폭발 없이 소멸한다.
    /// </summary>
    public void ProcessSwarmCrossfires(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        if (!SwarmCrossfireEnabled)
            return;

        SunOrbAttackState sunOrbAttacks = matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks;
        IReadOnlyList<SwarmArenaCombatTarget>? monsters = null;
        int shapesBefore = sunOrbAttacks.ShapeCount;
        for (int index = sunOrbAttacks.ShapeCount - 1; index >= 0; index--)
        {
            SwarmCrossfireShape shape = sunOrbAttacks.GetShapeAt(index);
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
            monsters ??= matchRuntimes.GetOrThrow(matchingId).Monsters.GetCombatTargets(matchingId);

            // 관통 (뱀서식): 이번 틱 구간에 걸린 표적 전부를 지나가며 때린다 —
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
                matchRuntimes.GetOrThrow(matchingId).Monsters.RecordMonsterAttackEvent(matchingId, monster.CombatTargetId);
                int monsterDamage = matchRuntimes.GetOrThrow(matchingId).CombatDamage.RollSwarmCriticalDamage(
                    shape.Damage, out bool critical);
                matchRuntimes.GetOrThrow(matchingId).CombatDamage.ApplySwarmMonsterHitNow(
                    monster.CombatTargetId, monster.MonsterId, shape.OwnerId,
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
                matchRuntimes.GetOrThrow(matchingId).CombatDamage.ApplySwarmShock(playerEliminations,
                    shape.OwnerId, shape.WeaponItemId, shape.Area, participant.PlayerId,
                    $"ORB_CROSSFIRE_HIT event={shape.EventId} shape=pierce anchor={shape.AnchorMonsterId}",
                    matchRuntimes.GetOrThrow(matchingId).GetAlivePlayers(), allSessions);
                if (matchRuntimes.GetOrThrow(matchingId).IsEnded) return;
                ApplySwarmSunBurn(
                    matchingId, shape.OwnerId, shape.WeaponItemId, shape.Area,
                    participant.PlayerId, nowUtc, aliveSessions);
            }

            if (front < sweepEnd)
                continue;

            sunOrbAttacks.RemoveShapeAt(index);
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
                eventLogs.LogSystem(
                    matchingId, $"ORB_CROSSFIRE_VANISH event={shape.EventId} owner={shape.OwnerId}");
            }
        }

        // 봇 회피 스냅샷 — 이번 틱에 소멸·폭발로 줄었으면 다시 발행한다.
        if (sunOrbAttacks.ShapeCount != shapesBefore)
            sunOrbAttacks.PublishDodgeSnapshot();
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
        eventLogs.LogSystem(
            matchingId,
            $"ORB_CROSSFIRE_DETONATE event={shape.EventId} owner={shape.OwnerId} " +
            $"at=({detonation.X:F2},{detonation.Y:F2}) radius={shape.BlastRadius:F2} visualOnly=true");
    }

    private static void BroadcastSwarmCrossfireDetonation(
        SwarmCrossfireShape shape, Vector3f detonation, List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUN_ORB_ATTACK);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUN_ORB_ATTACK
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
            if (session.PlayerId.HasValue && !session.Player.IsEliminated && session.Player.CurrentArea == shape.Area)
                session.TrySend(packet);
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

    // 벽 탐색 표본 간격(바닥면 단위) — 셀(1) 대비 충분히 촘촘하다.
    private const float SwarmCrossfireWallProbeStep = 0.2f;
    // 벽 탐색 상한(바닥면 단위) — 가장 큰 구역 대각보다 넉넉히 크다. 여기까지 경계를 못 찾으면
    // (구역 데이터 이상) 폭발 없이 소멸하는 안전망으로 떨어진다.
    private const float SwarmCrossfireMaxGroundLength = 40f;

    /// <summary>
    ///     원점에서 바닥면 단위 방향(unitX, unitY)으로 표본을 전진시키며 첫 구역 밖 셀(구역 경계·벽)까지의
    ///     거리를 찾는다 — 없으면 maxLength. 이동 불가 셀이 아니라 구역 소속을 보는 이유 (유저
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

    // 플레이어 몸통 캡슐("큰 캡슐"로 결정, 화면 정합 보정): 판정
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

    // 몹 몸통("충돌하면 안 맞고 비껴가면 맞는다" 제보): 표시 차선이 부양(+0.8) 높이라 지면 판정 그대로면 화면
    // 겹침과 판정이 반전된다 — 플레이어와 같은 화면 정합 표본을 쓴다. 몸 높이는 소형 몹 기준 0.6.
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

    // 충격 면역은 없다 — 태양 다발 화망에서 첫 발 이후가 소리 없이 관통하면 "피격박스가 안 맞는" 오독이 된다.
    // 표시 = 판정: 지나간 발은 다 맞는다. 한 발이 같은 사람을 두 번 치는 것은 발 단위 HitVictims(교차사격)·틱 주기(칼날)가 막는다.

    /// <summary>화상 부여·갱신 — 첫 틱은 1초 뒤(직격과 같은 프레임에 겹치지 않게). HUD 통지 포함.</summary>
    private void ApplySwarmSunBurn(
        long matchingId, long ownerId, int weaponItemId, AreaType area, long victimId,
        DateTime nowUtc, List<GameClientSession> aliveSessions)
    {
        matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.SetSunBurn(
            victimId,
            ownerId,
            weaponItemId,
            area,
            nowUtc,
            Config.SWARM_SUN_BURN_SECONDS,
            Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS);
        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        if (victimSession == null || !victimSession.PlayerId.HasValue || ownerId == 0) return;

        using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
        {
            SourcePlayerId = ownerId,
            TargetPlayerId = victimId,
            AreaType = area,
            Effect = CombatStatusEffectKind.SunBurn,
            DurationMs = (int)(Config.SWARM_SUN_BURN_SECONDS * 1000f)
        });
        victimSession.TrySend(packet);
    }

    /// <summary>화상 틱 정산 — 초당 한 번, 충격의 0.2배. 지속이 끝나면 걷는다.</summary>
    public void ProcessSwarmSunBurns(
        long matchingId,
        DateTime nowUtc,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.ProcessSunBurns(
            nowUtc,
            Config.SWARM_SUN_BURN_TICK_INTERVAL_SECONDS,
            (victimId, burn) =>
                matchRuntimes.GetOrThrow(matchingId).CombatDamage.ApplySwarmShock(playerEliminations, burn.OwnerId, burn.WeaponItemId, burn.Area,
                    victimId, "SUN_BURN_TICK", matchRuntimes.GetOrThrow(matchingId).GetAlivePlayers(), allSessions,
                    Config.SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER, isPeriodicDamage: true));
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
        // 화면 정합(명중과 같은 몸통 표본): 중앙 1점만 세면 대각 축의 수직 성분(0.7)이 도달 반경(0.65)을 넘어
        // 실제로 맞을 몹이 카운트에서 빠진다 — 좁은 복도의 세로 후보가 0마리로 집계돼 선택되지 않는다.
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

    /// <summary>1초 창 안에 같은 표적이 교차사격을 두 발 이상 맞으면 crossfire_converge로 남긴다.</summary>
    private void TrackSwarmCrossfireConvergence(long matchingId, long targetId, DateTime nowUtc)
    {
        SwarmCrossfireConvergenceObservation observation =
            matchRuntimes.GetOrThrow(matchingId).SunOrbAttacks.TrackConvergence(targetId, nowUtc);
        if (observation.HitCount >= 2)
        {
            eventLogs.LogSystem(
                matchingId,
                $"crossfire_converge target={targetId} hits={observation.HitCount} " +
                $"windowMs={observation.WindowMilliseconds:F0}");
        }
    }
}
