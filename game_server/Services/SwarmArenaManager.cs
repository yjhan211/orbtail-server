using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     #217 스웜 회피 P0-a. 잔상을 공급이 아니라 압력으로 쓴다. 단일 열린 공간에서
///     패턴 스폰(링 조임·방향 돌진·포위)된 잔상이 플레이어를 추적하고, 접촉이 피해를 준다.
///     스폰 직후에는 예고 시간 동안 정지·무해 상태로 서 있어 텔레그래프 역할을 한다.
/// </summary>
public sealed class SwarmArenaManager
{
    public const int MatchDurationSeconds = 90;
    public const int MonsterMaxHealth = 12;

    // 실측(2026-08-05): 30 + 무적 0.6초 조합은 18초 생존으로 끝났다. 연속 접촉 기준
    // 최소 사망 시간이 판 길이의 1/6을 넘도록 24 × 0.8초로 완화한다 (420/24×0.8 ≈ 14초).
    public const int ContactDamage = 24;

    // 몬스터별 쿨다운만 있으면 무리에 겹칠 때 마릿수만큼 중첩 피격되어 1~2초 만에 죽는다.
    // 뱀서 표준대로 참가자 측 피격 무적을 둔다: 한 입은 아프게, 무리는 초당 한 입만.
    public const float ContactImmunitySeconds = 0.8f;

    // 접촉은 실제 겹침 수준에서만 성립해야 한다. 산포 정지 지점보다 크고
    // 시각적 비접촉 거리보다 작게 유지할 것. 서버 위치는 클라이언트 예측보다
    // 늦으므로 회피자에게 후한 쪽이 맞다.
    public const float ContactRange = 0.45f;
    public const float ContactCooldownSeconds = 1f;
    public const float MonsterMoveSpeed = 4.2f;
    public const float RingTelegraphSeconds = 1f;
    public const float RushTelegraphSeconds = 1f;
    public const float EncircleTelegraphSeconds = 1.5f;
    public const int RingSpawnCount = 10;
    public const int RushSpawnCount = 8;
    public const int EncircleSpawnCount = 8;

    // PvP는 압박·마무리 보조다. 킬의 주 경로는 스웜(접촉 30)이어야 한다.
    public const int PvpDamage = 3;

    // 개봉 소음 유인 반경: 채집을 시작하면 이 반경의 잔상이 개봉자에게 몰린다.
    // 게이지 1.5~2초 + 잔상 속도 4.2면 최대 3초대에 도착 — 개봉이 곧 리스크 창이 된다.
    public const float ExploreAttractRadius = 14f;

    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const float RingRadius = 9f;
    private const float RushDistance = 11f;
    private const float RushLateralSpread = 1.6f;
    private const float EncircleRadius = 4.5f;
    private const float ScatterRadius = 0.2f;
    private const float RetargetStickinessSquared = 1.5625f;
    private const float BotDangerRadius = 4f;

    // 사거리(7) 밖 + 포위 스폰 반경(4.5) 밖에서 배회해야 상시 칩딜·패턴 즉사를 피한다.
    private const float BotHoverDistance = 8f;
    private const float BotFleeDistance = 5f;
    private const double FirstPatternDelaySeconds = 3d;
    private const double PatternIntervalStartSeconds = 10d;
    private const double PatternIntervalEndSeconds = 6d;
    private const double DeadPruneAfterSeconds = 3d;

    /// <summary>밀도 단계: 30초마다 동시 생존 상한을 올린다. 성능 계측과 병행 인상한다.</summary>
    private static readonly int[] DensityCaps = [20, 40, 60];

    private readonly ConcurrentDictionary<long, MatchState> _matches = new();
    private readonly Func<DateTime> _utcNow;

    public SwarmArenaManager(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool HasMatching(long matchingId) => _matches.ContainsKey(matchingId);

    public bool InitializeMatching(
        long matchingId,
        long playerId,
        AreaType area,
        Cell centerCell,
        DateTime startsAtUtc)
    {
        if (matchingId <= 0 || playerId <= 0)
            return false;

        var state = new MatchState
        {
            MatchingId = matchingId,
            PlayerId = playerId,
            Area = area,
            CenterPosition = MapCoordinateConverter.CellToWorld(MapId.School, centerCell),
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = startsAtUtc.AddSeconds(MatchDurationSeconds),
            NextPatternAtUtc = startsAtUtc.AddSeconds(FirstPatternDelaySeconds),
            LastTickAtUtc = startsAtUtc,
            Rng = new Random(unchecked((int)(matchingId ^ 0x5A7A_17)))
        };
        return _matches.TryAdd(matchingId, state);
    }

    public SwarmArenaTickResult Tick(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerSpatial> participants,
        DateTime? nowUtc = null)
    {
        var result = new SwarmArenaTickResult();
        if (!_matches.TryGetValue(matchingId, out var state))
            return result;

        DateTime now = nowUtc ?? _utcNow();
        lock (state.SyncRoot)
        {
            if (state.Ended)
            {
                result.MatchEnded = true;
                result.Survived = state.Survived;
                return result;
            }

            double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
            state.LastTickAtUtc = now;
            state.LastParticipants = participants.ToArray();

            if (now >= state.EndsAtUtc)
            {
                state.Ended = true;
                state.Survived = true;
                result.MatchEnded = true;
                result.Survived = true;
                return result;
            }

            // 패턴은 검증 대상인 사람을 중심으로 소환한다. 잔상은 소환 후 가장 가까운
            // 참가자를 문다 — 스웜을 상대 쪽으로 끌고 가는 플레이(몹 끌기)의 근거.
            Vector3f patternCenter = state.CenterPosition;
            foreach (var participant in state.LastParticipants)
            {
                if (participant.PlayerId != state.PlayerId)
                    continue;
                patternCenter = participant.Position;
                break;
            }

            SpawnDuePattern(state, patternCenter, now, result);

            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || now < monster.ActivatesAtUtc)
                    continue;

                if (!TryResolveChaseTarget(monster, state.LastParticipants, out var chaseTarget))
                    continue;

                MoveTowardPlayer(monster, chaseTarget.Position, deltaSeconds);
                if (now < monster.NextContactAtUtc)
                    continue;

                foreach (var participant in state.LastParticipants)
                {
                    float dx = monster.Position.X - participant.Position.X;
                    float dy = monster.Position.Y - participant.Position.Y;
                    if (dx * dx + dy * dy > ContactRange * ContactRange)
                        continue;
                    if (state.ContactImmuneUntilUtc.TryGetValue(participant.PlayerId, out var immuneUntil) &&
                        now < immuneUntil)
                        continue;

                    monster.NextContactAtUtc = now.AddSeconds(ContactCooldownSeconds);
                    state.ContactImmuneUntilUtc[participant.PlayerId] =
                        now.AddSeconds(ContactImmunitySeconds);
                    if (participant.PlayerId == state.PlayerId)
                    {
                        state.HitsTaken++;
                        state.PatternHits[monster.Pattern] =
                            state.PatternHits.GetValueOrDefault(monster.Pattern) + 1;
                    }

                    result.PlayerDamage.Add(new SpotArenaPlayerDamage(
                        monster.MonsterId,
                        participant.PlayerId,
                        state.Area,
                        ContactDamage));
                    break;
                }
            }

            PruneDeadMonsters(state, now);
            return result;
        }
    }

    /// <summary>
    ///     가장 가까운 참가자를 쫓되, 기존 목표가 최근접의 1.25배 거리 안이면 유지한다.
    ///     히스테리시스 없이 매 틱 최근접으로 갈아타면 두 참가자 중간에서 왕복 진동한다.
    /// </summary>
    private static bool TryResolveChaseTarget(
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        out SpotArenaPlayerSpatial target)
    {
        target = default;
        if (participants.Count == 0)
            return false;

        int nearestIndex = -1;
        float nearestSquared = float.MaxValue;
        int currentIndex = -1;
        float currentSquared = float.MaxValue;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            float dx = participant.Position.X - monster.Position.X;
            float dy = participant.Position.Y - monster.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestSquared)
            {
                nearestSquared = distanceSquared;
                nearestIndex = index;
            }

            if (participant.PlayerId == monster.ChaseTargetPlayerId)
            {
                currentIndex = index;
                currentSquared = distanceSquared;
            }
        }

        if (currentIndex >= 0 && currentSquared <= nearestSquared * RetargetStickinessSquared)
        {
            target = participants[currentIndex];
            return true;
        }

        target = participants[nearestIndex];
        monster.ChaseTargetPlayerId = target.PlayerId;
        return true;
    }

    public void EndForDeath(long matchingId, DateTime? nowUtc = null)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return;
        lock (state.SyncRoot)
        {
            if (state.Ended)
                return;
            state.Ended = true;
            state.Survived = false;
            state.EndedAtUtc = nowUtc ?? _utcNow();
        }
    }

    public bool TryGetEndState(long matchingId, out bool survived)
    {
        survived = false;
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            survived = state.Survived;
            return state.Ended;
        }
    }

    public SwarmArenaDamageResult ApplyMonsterDamage(
        long matchingId,
        long combatTargetId,
        long attackerPlayerId,
        int damage)
    {
        if (damage <= 0 || !_matches.TryGetValue(matchingId, out var state))
            return SwarmArenaDamageResult.None;

        lock (state.SyncRoot)
        {
            var monster = state.Monsters.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId && candidate.Alive);
            if (monster == null)
                return SwarmArenaDamageResult.None;

            monster.Health = Math.Max(0, monster.Health - damage);
            bool killed = monster.Health == 0;
            if (killed)
            {
                monster.Alive = false;
                monster.DiedAtUtc = _utcNow();
                if (attackerPlayerId == state.PlayerId)
                    state.Kills++;
            }

            return new SwarmArenaDamageResult(true, killed, monster.MonsterId, monster.ToMonsterRuntimeInfo());
        }
    }

    /// <summary>개봉 소음: 반경 안 잔상이 개봉자를 새 추적 목표로 삼는다 (#217 P0-c 리스크 창).</summary>
    public void AttractSwarm(long matchingId, long playerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return;
        lock (state.SyncRoot)
        {
            bool found = false;
            Vector3f position = state.CenterPosition;
            foreach (var participant in state.LastParticipants)
            {
                if (participant.PlayerId != playerId)
                    continue;
                found = true;
                position = participant.Position;
                break;
            }

            if (!found)
                return;

            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive)
                    continue;
                float dx = monster.Position.X - position.X;
                float dy = monster.Position.Y - position.Y;
                if (dx * dx + dy * dy > ExploreAttractRadius * ExploreAttractRadius)
                    continue;
                monster.ChaseTargetPlayerId = playerId;
            }
        }
    }

    /// <summary>
    ///     봇 지시: 잔상 무리가 가까우면 무리 반대쪽으로 도망치고, 아니면 사람 근처를 배회한다.
    ///     회피 압력을 유지하면서 조우가 자연 발생하게 만드는 최소 행동이다.
    /// </summary>
    public SpotArenaBotDirective GetBotDirective(long matchingId, long botPlayerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SpotArenaBotDirective.None;

        lock (state.SyncRoot)
        {
            if (state.Ended)
                return SpotArenaBotDirective.None;

            bool botFound = false;
            Vector3f botPosition = state.CenterPosition;
            Vector3f humanPosition = state.CenterPosition;
            foreach (var participant in state.LastParticipants)
            {
                if (participant.PlayerId == botPlayerId)
                {
                    botFound = true;
                    botPosition = participant.Position;
                }
                else if (participant.PlayerId == state.PlayerId)
                {
                    humanPosition = participant.Position;
                }
            }

            if (!botFound)
                return SpotArenaBotDirective.None;

            DateTime now = _utcNow();
            float threatX = 0f, threatY = 0f;
            int threatCount = 0;
            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || now < monster.ActivatesAtUtc)
                    continue;
                float dx = monster.Position.X - botPosition.X;
                float dy = monster.Position.Y - botPosition.Y;
                if (dx * dx + dy * dy > BotDangerRadius * BotDangerRadius)
                    continue;
                threatX += monster.Position.X;
                threatY += monster.Position.Y;
                threatCount++;
            }

            Vector3f destination;
            SpotArenaBotMode mode;
            if (threatCount > 0)
            {
                float centroidX = threatX / threatCount;
                float centroidY = threatY / threatCount;
                float awayX = botPosition.X - centroidX;
                float awayY = botPosition.Y - centroidY;
                float length = MathF.Sqrt(awayX * awayX + awayY * awayY);
                if (length < 0.01f)
                {
                    awayX = 1f;
                    awayY = 0f;
                    length = 1f;
                }

                destination = new Vector3f(
                    botPosition.X + awayX / length * BotFleeDistance,
                    botPosition.Y + awayY / length * BotFleeDistance,
                    0f);
                mode = SpotArenaBotMode.Return;
            }
            else
            {
                // 사람 주위를 접선 방향으로 돈다. 재계획마다 60도씩 진행해 봇이
                // 제자리에 서 있지 않고 계속 궤도를 그리며 조우 압력을 만든다.
                float angle = MathF.Atan2(
                    botPosition.Y - humanPosition.Y,
                    botPosition.X - humanPosition.X) + 1.05f;
                destination = new Vector3f(
                    humanPosition.X + MathF.Cos(angle) * BotHoverDistance,
                    humanPosition.Y + MathF.Sin(angle) * BotHoverDistance,
                    0f);
                mode = SpotArenaBotMode.Escort;
            }

            destination = ClampToWalkable(destination, state.CenterPosition);
            return new SpotArenaBotDirective(
                mode,
                state.Area,
                MapCoordinateConverter.WorldToCell(MapId.School, destination),
                destination);
        }
    }

    public IReadOnlyList<MonsterRuntimeInfo> GetVisualStates(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return [];
        lock (state.SyncRoot)
            return state.Monsters.Values
                .Select(monster => monster.ToMonsterRuntimeInfo(state.Area))
                .ToArray();
    }

    public IReadOnlyList<SwarmArenaCombatTarget> GetCombatTargets(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return [];
        lock (state.SyncRoot)
        {
            DateTime now = _utcNow();
            return state.Monsters.Values
                .Where(monster => monster.Alive && now >= monster.ActivatesAtUtc)
                .Select(monster => new SwarmArenaCombatTarget(
                    monster.CombatTargetId,
                    state.Area,
                    monster.Position,
                    monster.MonsterId))
                .ToArray();
        }
    }

    public SwarmArenaSummary GetSummary(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SwarmArenaSummary.Empty;
        lock (state.SyncRoot)
        {
            DateTime endedAt = state.Ended && state.EndedAtUtc != default ? state.EndedAtUtc : _utcNow();
            return new SwarmArenaSummary(
                state.Survived,
                Math.Min(MatchDurationSeconds, (endedAt - state.StartsAtUtc).TotalSeconds),
                state.HitsTaken,
                state.Kills,
                state.PatternHits.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
        }
    }

    public void RemoveMatching(long matchingId) => _matches.TryRemove(matchingId, out _);

    private static void SpawnDuePattern(
        MatchState state,
        Vector3f playerPosition,
        DateTime now,
        SwarmArenaTickResult result)
    {
        if (now < state.NextPatternAtUtc)
            return;

        double elapsed = (now - state.StartsAtUtc).TotalSeconds;
        double interval = PatternIntervalStartSeconds +
                          (PatternIntervalEndSeconds - PatternIntervalStartSeconds) *
                          Math.Clamp(elapsed / MatchDurationSeconds, 0d, 1d);
        state.NextPatternAtUtc = now.AddSeconds(interval);

        int densityCap = DensityCaps[Math.Min(
            DensityCaps.Length - 1,
            (int)(elapsed / (MatchDurationSeconds / (double)DensityCaps.Length)))];
        int alive = state.Monsters.Values.Count(monster => monster.Alive);
        if (alive >= densityCap)
            return;

        var pattern = state.NextPattern;
        state.NextPattern = (SwarmPattern)(((int)pattern + 1) % 3);
        int budget = densityCap - alive;
        switch (pattern)
        {
            case SwarmPattern.Ring:
                SpawnRing(state, playerPosition, now, Math.Min(budget, RingSpawnCount), result);
                break;
            case SwarmPattern.Rush:
                SpawnRush(state, playerPosition, now, Math.Min(budget, RushSpawnCount), result);
                break;
            default:
                SpawnEncircle(state, playerPosition, now, Math.Min(budget, EncircleSpawnCount), result);
                break;
        }
    }

    private static void SpawnRing(
        MatchState state,
        Vector3f center,
        DateTime now,
        int count,
        SwarmArenaTickResult result)
    {
        for (int index = 0; index < count; index++)
        {
            float angle = (float)(index * Math.PI * 2d / count) +
                          (float)(state.Rng.NextDouble() * 0.4d - 0.2d);
            var position = new Vector3f(
                center.X + MathF.Cos(angle) * RingRadius,
                center.Y + MathF.Sin(angle) * RingRadius,
                0f);
            SpawnMonster(state, position, now, RingTelegraphSeconds, SwarmPattern.Ring, result);
        }
    }

    private static void SpawnRush(
        MatchState state,
        Vector3f center,
        DateTime now,
        int count,
        SwarmArenaTickResult result)
    {
        float direction = (float)(state.Rng.NextDouble() * Math.PI * 2d);
        float lateralX = -MathF.Sin(direction);
        float lateralY = MathF.Cos(direction);
        for (int index = 0; index < count; index++)
        {
            float lateral = (index - (count - 1) * 0.5f) * RushLateralSpread;
            float depth = (float)(state.Rng.NextDouble() * 2d);
            var position = new Vector3f(
                center.X + MathF.Cos(direction) * (RushDistance + depth) + lateralX * lateral,
                center.Y + MathF.Sin(direction) * (RushDistance + depth) + lateralY * lateral,
                0f);
            SpawnMonster(state, position, now, RushTelegraphSeconds, SwarmPattern.Rush, result);
        }
    }

    private static void SpawnEncircle(
        MatchState state,
        Vector3f center,
        DateTime now,
        int count,
        SwarmArenaTickResult result)
    {
        for (int index = 0; index < count; index++)
        {
            float angle = (float)(index * Math.PI * 2d / count);
            var position = new Vector3f(
                center.X + MathF.Cos(angle) * EncircleRadius,
                center.Y + MathF.Sin(angle) * EncircleRadius,
                0f);
            SpawnMonster(state, position, now, EncircleTelegraphSeconds, SwarmPattern.Encircle, result);
        }
    }

    private static void SpawnMonster(
        MatchState state,
        Vector3f position,
        DateTime now,
        float telegraphSeconds,
        SwarmPattern pattern,
        SwarmArenaTickResult result)
    {
        position = ClampToWalkable(position, state.CenterPosition);
        int serial = state.NextSerial++;
        var monster = new MonsterRuntime
        {
            MonsterId = FirstMonsterId + serial,
            CombatTargetId = FirstCombatTargetId - serial,
            Pattern = pattern,
            Position = position,
            Health = MonsterMaxHealth,
            Alive = true,
            ActivatesAtUtc = now.AddSeconds(telegraphSeconds),
            NextContactAtUtc = now,
            ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
            SummonStoneReward = state.NextMonsterGrantsSummonStone ? 1 : 0
        };
        state.NextMonsterGrantsSummonStone = !state.NextMonsterGrantsSummonStone;
        state.Monsters[monster.MonsterId] = monster;
        result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo(state.Area));
    }

    private static void MoveTowardPlayer(MonsterRuntime monster, Vector3f playerPosition, double deltaSeconds)
    {
        // 개체별 산포 오프셋으로 플레이어 주변을 둘러싸게 한다. 열 형성 방지.
        var target = new Vector3f(
            playerPosition.X + MathF.Cos(monster.ScatterAngle) * ScatterRadius,
            playerPosition.Y + MathF.Sin(monster.ScatterAngle) * ScatterRadius,
            0f);
        float dx = target.X - monster.Position.X;
        float dy = target.Y - monster.Position.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance < 0.05f)
            return;

        float step = (float)(MonsterMoveSpeed * deltaSeconds);
        if (step > distance)
            step = distance;
        var proposed = new Vector3f(
            monster.Position.X + dx / distance * step,
            monster.Position.Y + dy / distance * step,
            0f);

        Cell proposedCell = MapCoordinateConverter.WorldToCell(MapId.School, proposed);
        if (GameMapData.IsMoveablePosition(MapId.School, proposedCell))
        {
            monster.Position = proposed;
            return;
        }

        // 벽이면 축별로 미끄러진다.
        var slideX = new Vector3f(proposed.X, monster.Position.Y, 0f);
        if (GameMapData.IsMoveablePosition(
                MapId.School, MapCoordinateConverter.WorldToCell(MapId.School, slideX)))
        {
            monster.Position = slideX;
            return;
        }

        var slideY = new Vector3f(monster.Position.X, proposed.Y, 0f);
        if (GameMapData.IsMoveablePosition(
                MapId.School, MapCoordinateConverter.WorldToCell(MapId.School, slideY)))
            monster.Position = slideY;
    }

    private static Vector3f ClampToWalkable(Vector3f position, Vector3f center)
    {
        if (GameMapData.IsMoveablePosition(
                MapId.School, MapCoordinateConverter.WorldToCell(MapId.School, position)))
            return position;

        // 스폰 위치가 보행 불가면 중심 쪽으로 당기며 첫 보행 가능 지점을 찾는다.
        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            var candidate = new Vector3f(
                position.X + (center.X - position.X) * t,
                position.Y + (center.Y - position.Y) * t,
                0f);
            if (GameMapData.IsMoveablePosition(
                    MapId.School, MapCoordinateConverter.WorldToCell(MapId.School, candidate)))
                return candidate;
        }

        return center;
    }

    private static void PruneDeadMonsters(MatchState state, DateTime now)
    {
        var expired = state.Monsters.Values
            .Where(monster => !monster.Alive &&
                              (now - monster.DiedAtUtc).TotalSeconds > DeadPruneAfterSeconds)
            .Select(monster => monster.MonsterId)
            .ToList();
        foreach (int monsterId in expired)
            state.Monsters.Remove(monsterId);
    }

    private sealed class MatchState
    {
        public object SyncRoot { get; } = new();
        public long MatchingId { get; init; }
        public long PlayerId { get; init; }
        public AreaType Area { get; init; }
        public Vector3f CenterPosition { get; init; } = new(0f, 0f, 0f);
        public DateTime StartsAtUtc { get; init; }
        public DateTime EndsAtUtc { get; init; }
        public DateTime EndedAtUtc { get; set; }
        public DateTime LastTickAtUtc { get; set; }
        public DateTime NextPatternAtUtc { get; set; }
        public SwarmPattern NextPattern { get; set; } = SwarmPattern.Ring;
        public Dictionary<int, MonsterRuntime> Monsters { get; } = new();
        public Random Rng { get; init; } = new();
        public int NextSerial { get; set; }
        public bool Ended { get; set; }
        public bool Survived { get; set; }
        public bool NextMonsterGrantsSummonStone { get; set; } = true;
        public SpotArenaPlayerSpatial[] LastParticipants { get; set; } = [];
        public Dictionary<long, DateTime> ContactImmuneUntilUtc { get; } = new();
        public int HitsTaken { get; set; }
        public int Kills { get; set; }
        public Dictionary<SwarmPattern, int> PatternHits { get; } = new();
    }

    private sealed class MonsterRuntime
    {
        public int MonsterId { get; init; }
        public long CombatTargetId { get; init; }
        public SwarmPattern Pattern { get; init; }
        public Vector3f Position { get; set; } = new(0f, 0f, 0f);
        public int Health { get; set; }
        public bool Alive { get; set; }
        public DateTime ActivatesAtUtc { get; init; }
        public DateTime NextContactAtUtc { get; set; }
        public DateTime DiedAtUtc { get; set; }
        public float ScatterAngle { get; init; }
        public int SummonStoneReward { get; init; }
        public long ChaseTargetPlayerId { get; set; }

        public MonsterRuntimeInfo ToMonsterRuntimeInfo(AreaType area = AreaType.Ground) => new()
        {
            MonsterId = MonsterId,
            AreaType = area,
            PositionX = Position.X,
            PositionY = Position.Y,
            MaxHealth = MonsterMaxHealth,
            CurrentHealth = Health,
            IsAlive = Alive,
            RewardItemId = Pattern switch
            {
                SwarmPattern.Ring => 107000010,
                SwarmPattern.Rush => 107000020,
                _ => 107000030
            },
            IsCore = false,
            SummonStoneReward = SummonStoneReward
        };
    }
}

public enum SwarmPattern
{
    Ring = 0,
    Rush = 1,
    Encircle = 2
}

public sealed class SwarmArenaTickResult
{
    public List<SpotArenaPlayerDamage> PlayerDamage { get; } = new();
    public List<MonsterRuntimeInfo> SpawnedMonsters { get; } = new();
    public bool MatchEnded { get; set; }
    public bool Survived { get; set; }
}

public readonly record struct SwarmArenaDamageResult(
    bool Applied,
    bool Killed,
    int MonsterId,
    MonsterRuntimeInfo? MonsterState)
{
    public static SwarmArenaDamageResult None => new(false, false, 0, null);
}

public readonly record struct SwarmArenaCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    int MonsterId);

public readonly record struct SwarmArenaSummary(
    bool Survived,
    double SurvivalSeconds,
    int HitsTaken,
    int Kills,
    IReadOnlyDictionary<string, int> PatternHits)
{
    public static SwarmArenaSummary Empty => new(false, 0d, 0, 0, new Dictionary<string, int>());
}
