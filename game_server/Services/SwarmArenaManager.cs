using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     #217 스웜 디렉터. 매치 수명은 기존 서바이버 로얄 흐름(탈락·최후 1인·타이머)이 소유하고,
///     이 매니저는 잔상 스웜만 담당한다: 참가자별 패턴 스폰(구역 프로파일), 같은 구역 추적,
///     접촉 피해, 개봉 소음 유인. 잔상은 공급이 아니라 회피해야 하는 압력이다.
/// </summary>
public sealed class SwarmArenaManager
{
    // 성장 체감 재앵커 (2026-08-07): T1 오브 DPS 20 기준 SB ÷15 환산. 해골도 2대는 맞아야
    // 하고, 탈주(100)는 시작 스쿼드론 5초+ — 오브가 늘수록 브루저가 눈에 띄게 빨리 녹는다.
    public const int MonsterMaxHealth = 20;

    // 실측(2026-08-05): 30 + 무적 0.6초 조합은 18초 생존으로 끝났다. 연속 접촉 기준
    // 최소 사망 시간이 충분히 길도록 24 × 0.8초를 유지한다.
    public const int ContactDamage = 24;

    // 시작방 몹은 약하게: 봇 포함 8인 전원이 초반 2팩을 버티고 조우 지점까지 살아나가야
    // 조우 구도가 성립한다. 위험 경사는 시작방(약) → 조우 지점·외곽(강)으로 유지.
    public const int StartRoomContactDamage = 12;

    // 몬스터별 쿨다운만 있으면 무리에 겹칠 때 마릿수만큼 중첩 피격되어 1~2초 만에 죽는다.
    // 뱀서 표준대로 참가자 측 피격 무적을 둔다: 한 입은 아프게, 무리는 초당 한 입만.
    public const float ContactImmunitySeconds = 0.8f;

    // 접촉은 실제 겹침 수준에서만 성립해야 한다. 서버 위치는 클라이언트 예측보다
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

    // PvP는 압박·마무리 보조다. 킬의 주 경로는 스웜(접촉 24)이어야 한다.
    public const int PvpDamage = 3;

    // 개봉 소음 유인 반경: 채집을 시작하면 같은 구역 이 반경의 잔상이 개봉자에게 몰린다.
    public const float ExploreAttractRadius = 14f;

    // #219 SB 클론 M1: 몹 개시권을 플레이어에게. 몹은 캠프에 고정되고, 근접하거나
    // 맞았을 때만 리쉬 안에서 반격 추격하며, 리쉬를 벗어나면 캠프로 돌아가 잠든다.
    // 방은 조용하고 위험은 선택이다 — 켜면 추적 스웜 디렉터(패턴 스폰)는 쉰다.
    public static readonly bool CampModeEnabled = true;
    public const float CampAggroRadius = 2.5f;
    // 리쉬는 봇 사격 대역(5~7)보다 짧게 — 추격이 빨리 끊겨야 카이팅 사이클이 성립한다.
    public const float CampLeashRadius = 5.5f;
    private const int CampsPerArea = 3;
    private const int CampMonstersPerCamp = 3;
    // 스폰 셀(방 중앙)과 캠프 사이 안전 이격 — 스폰 포켓은 SB처럼 비워 둔다.
    private const float CampAnchorRadius = 6f;
    private const float CampScatterRadius = 1.2f;
    private const double CampRespawnSeconds = 45d;
    private const float CampReturnArriveDistance = 0.4f;

    // #219 SB 몬스터 4종 (원작 스펙 ÷25 환산, 잼 보류 — 보상은 소환석만).
    // 해골: 무해한 코인 파밍 무리. 다트: 원거리 단발. 탈주: 접촉 강펀치 브루저. 볼러: 범위 투척.
    public const float BowlerSplashRadius = 1.5f;

    public static (int MaxHp, int OrbDamage, float AttackRange, float AttackCooldownSeconds, int StoneReward)
        GetKindStats(SwarmMonsterKind kind) => kind switch
    {
        SwarmMonsterKind.DartGoblin => (27, 2, 5f, 2f, 1),
        SwarmMonsterKind.RunawayGoblin => (100, 5, ContactRange, 1.2f, 4),
        SwarmMonsterKind.Bowler => (80, 2, 4.5f, 2.5f, 4),
        _ => (MonsterMaxHealth, 1, ContactRange, ContactCooldownSeconds, 1)
    };

    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const float RingRadius = 9f;
    private const float RushDistance = 11f;
    private const float RushLateralSpread = 1.6f;
    private const float EncircleRadius = 4.5f;
    private const float ScatterRadius = 0.2f;
    private const float RetargetStickinessSquared = 1.5625f;
    // 아이소 월드 스케일에서 방의 세로 폭은 ~2.5유닛에 불과하다. 이동 목표가 방을
    // 벗어나면 구역 클램프로 제자리 회귀해 봇이 서 있는 것처럼 보인다 — 짧게 잡는다.
    // 캠프 모드: 위험 반경 5 / 사격 대역 5~7 — 깨어난 몹이 5에 오면 물러나고,
    // 리쉬(5.5)가 곧 추격을 끊어 다시 설 자리가 생긴다.
    private const float BotDangerRadius = 5f;
    private const float BotFleeDistance = 5f;
    private const float BotRoamDistance = 3f;
    private const double FirstPatternDelaySeconds = 3d;
    private const double StartRoomFirstPatternDelaySeconds = 1.5d;
    private const double StartRoomSecondPackDelaySeconds = 11.5d;
    private const int StartRoomPackLimit = 2;
    private const double DeadPruneAfterSeconds = 3d;

    private static readonly HashSet<AreaType> StartRooms =
        SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToHashSet();

    // M4 격화: 폐쇄 웨이브와 동기화된 시간 단계. 접촉 데미지는 올리지 않는다 —
    // TTK가 아니라 밀도·페이스·이속만 조인다 (결정 브리프 2026-08-06).
    private const double EscalationStage1AtSeconds = 120d;
    private const double EscalationStage2AtSeconds = 230d;
    private const float EscalationStage2MoveSpeedMultiplier = 1.1f;

    /// <summary>폐쇄된 구역은 신규 스폰을 멈춘다 — 잔존 몹은 이주로 처리된다.</summary>
    public Func<long, AreaType, bool>? IsAreaClosedResolver { get; set; }

    private static int GetEscalationStage(double elapsedSeconds) =>
        elapsedSeconds >= EscalationStage2AtSeconds ? 2 :
        elapsedSeconds >= EscalationStage1AtSeconds ? 1 : 0;

    private readonly ConcurrentDictionary<long, MatchState> _matches = new();
    private readonly Func<DateTime> _utcNow;

    public SwarmArenaManager(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool HasMatching(long matchingId) => _matches.ContainsKey(matchingId);

    public bool InitializeMatching(long matchingId, long humanPlayerId, DateTime startsAtUtc)
    {
        // 봇 전용 검증 매치는 대표 참가자가 봇(음수 id)이다 — 0만 거부한다.
        if (matchingId <= 0 || humanPlayerId == 0)
            return false;

        var state = new MatchState
        {
            MatchingId = matchingId,
            HumanPlayerId = humanPlayerId,
            StartsAtUtc = startsAtUtc,
            LastTickAtUtc = startsAtUtc,
            Rng = new Random(unchecked((int)(matchingId ^ 0x5A7A_17)))
        };
        return _matches.TryAdd(matchingId, state);
    }

    /// <summary>
    ///     구역별 스웜 프로파일 (#217 8인 맵 역할). 시작방은 저위험 성장, 복도는 무스폰
    ///     이동·조우 통로, 운동장은 수렴점, 그 외 대형 공간은 고위험 성장로다.
    /// </summary>
    private static (int DensityCap, double IntervalSeconds) GetAreaProfile(AreaType area)
    {
        // SB 클론(캠프 모드): 균질 밀도 — 회랑 밴드(테라스=Corridor)에도 캠프가 선다.
        // 원본 맵의 링·광장 주변에도 몹 수풀이 고르게 깔려 있다 (역기획서 철학 ④).
        if (area == AreaType.Corridor)
            return CampModeEnabled ? (12, 9d) : (0, 0d);
        if (area == AreaType.Ground)
            return (14, 9d);
        // 시작방은 정확히 2팩(개전 1.5초 + 약 13초)만 주고 완전히 마른다 — 방은 유한
        // 콘텐츠고, 두 팩(약 18킬 = 18석)이면 방 스팟 2개를 열고 떠날 여비까지 나온다.
        if (StartRooms.Contains(area))
            return (12, StartRoomSecondPackDelaySeconds);
        return (16, 7d);
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
            double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
            state.LastTickAtUtc = now;
            state.LastParticipants = participants.ToArray();

            // 격화 2단계: 이속만 소폭 상승 — 접촉 데미지는 불변 (M4).
            double moveDeltaSeconds = GetEscalationStage((now - state.StartsAtUtc).TotalSeconds) >= 2
                ? deltaSeconds * EscalationStage2MoveSpeedMultiplier
                : deltaSeconds;

            if (CampModeEnabled)
            {
                foreach (var participant in state.LastParticipants)
                    EnsureAreaCamps(state, participant.Area, now, result);
            }
            else
            {
                foreach (var participant in state.LastParticipants)
                    SpawnDueParticipantPattern(state, participant, now, result);
            }

            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || now < monster.ActivatesAtUtc)
                    continue;

                if (CampModeEnabled)
                {
                    // 잠든 몹도 접촉 판정은 받는다 — 걸어 들어와 부딪히면 그게 개전이다.
                    UpdateCampMonsterMovement(monster, state.LastParticipants, moveDeltaSeconds);
                }
                else
                {
                    if (!TryResolveChaseTarget(monster, state.LastParticipants, out var chaseTarget))
                        continue;
                    MoveTowardPlayer(monster, chaseTarget.Position, moveDeltaSeconds);
                }

                if (now < monster.NextContactAtUtc)
                    continue;

                // 잠든 원거리 몹은 저격하지 않는다 — 부딪힘(접촉 반경)만 개전이 된다.
                float attackRange = monster.Aggro ? monster.AttackRangeValue : ContactRange;
                foreach (var participant in state.LastParticipants)
                {
                    if (participant.Area != monster.Area)
                        continue;
                    float dx = monster.Position.X - participant.Position.X;
                    float dy = monster.Position.Y - participant.Position.Y;
                    if (dx * dx + dy * dy > attackRange * attackRange)
                        continue;
                    if (state.ContactImmuneUntilUtc.TryGetValue(participant.PlayerId, out var immuneUntil) &&
                        now < immuneUntil)
                        continue;

                    monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
                    state.ContactImmuneUntilUtc[participant.PlayerId] =
                        now.AddSeconds(ContactImmunitySeconds);
                    if (CampModeEnabled)
                    {
                        // 부딪힘도 개전이다 — 맞은 캠프 몹이 깨어난다.
                        monster.Aggro = true;
                        monster.ChaseTargetPlayerId = participant.PlayerId;
                    }
                    if (participant.PlayerId == state.HumanPlayerId)
                    {
                        state.HitsTaken++;
                        state.PatternHits[monster.Pattern] =
                            state.PatternHits.GetValueOrDefault(monster.Pattern) + 1;
                    }

                    result.PlayerDamage.Add(new SpotArenaPlayerDamage(
                        monster.MonsterId,
                        participant.PlayerId,
                        monster.Area,
                        monster.ContactDamageValue));

                    // 볼러 스플래시: 주 대상 주변까지 함께 맞는다 — 뭉치기 견제.
                    if (monster.Kind == SwarmMonsterKind.Bowler)
                    {
                        foreach (var splashed in state.LastParticipants)
                        {
                            if (splashed.PlayerId == participant.PlayerId ||
                                splashed.Area != monster.Area)
                                continue;
                            float sx = splashed.Position.X - participant.Position.X;
                            float sy = splashed.Position.Y - participant.Position.Y;
                            if (sx * sx + sy * sy > BowlerSplashRadius * BowlerSplashRadius)
                                continue;
                            if (state.ContactImmuneUntilUtc.TryGetValue(splashed.PlayerId, out var splashImmune) &&
                                now < splashImmune)
                                continue;
                            state.ContactImmuneUntilUtc[splashed.PlayerId] =
                                now.AddSeconds(ContactImmunitySeconds);
                            result.PlayerDamage.Add(new SpotArenaPlayerDamage(
                                monster.MonsterId,
                                splashed.PlayerId,
                                monster.Area,
                                monster.ContactDamageValue));
                        }
                    }

                    break;
                }
            }

            PruneDeadMonsters(state, now);
            return result;
        }
    }

    /// <summary>
    ///     가장 가까운 같은 구역 참가자를 쫓되, 기존 목표가 최근접의 1.25배 거리 안이면 유지한다.
    ///     같은 구역에 아무도 없으면 그 자리에 서서 구역 위험물로 남는다.
    /// </summary>
    private static bool TryResolveChaseTarget(
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        out SpotArenaPlayerSpatial target)
    {
        target = default;
        int nearestIndex = -1;
        float nearestSquared = float.MaxValue;
        int currentIndex = -1;
        float currentSquared = float.MaxValue;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
                continue;
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

        if (nearestIndex < 0)
            return false;

        if (currentIndex >= 0 && currentSquared <= nearestSquared * RetargetStickinessSquared)
        {
            target = participants[currentIndex];
            return true;
        }

        target = participants[nearestIndex];
        monster.ChaseTargetPlayerId = target.PlayerId;
        return true;
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

            if (CampModeEnabled)
            {
                // 반격 개전: 맞은 몹과 같은 캠프 동료가 함께 깨어난다.
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = attackerPlayerId;
                foreach (var mate in state.Monsters.Values)
                {
                    if (!mate.Alive || mate.Aggro ||
                        mate.Area != monster.Area || mate.CampIndex != monster.CampIndex)
                        continue;
                    mate.Aggro = true;
                    mate.ChaseTargetPlayerId = attackerPlayerId;
                }
            }

            monster.Health = Math.Max(0, monster.Health - damage);
            bool killed = monster.Health == 0;
            if (killed)
            {
                monster.Alive = false;
                monster.DiedAtUtc = _utcNow();
                if (attackerPlayerId == state.HumanPlayerId)
                    state.Kills++;
            }

            return new SwarmArenaDamageResult(true, killed, monster.MonsterId, monster.ToMonsterRuntimeInfo());
        }
    }

    /// <summary>
    ///     M4 폐쇄 이주: 폐쇄된 구역의 잔존 스웜을 다음 구역으로 재배치한다.
    ///     추적이 아니라 디렉터의 재배치다 — 목적지에서 스폰 텔레그래프를 다시 거치고,
    ///     추적 대상도 초기화된다. "같은 구역만 추적" 규칙은 불변.
    /// </summary>
    public IReadOnlyList<MonsterRuntimeInfo> EvacuateArea(
        long matchingId,
        AreaType from,
        AreaType to,
        DateTime? nowUtc = null)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return Array.Empty<MonsterRuntimeInfo>();

        DateTime now = nowUtc ?? _utcNow();
        var moved = new List<MonsterRuntimeInfo>();
        lock (state.SyncRoot)
        {
            var anchorCell = GameMapData.GetAreaSpawnCell(MapId.School, to);
            var anchor = MapCoordinateConverter.CellToWorld(MapId.School, anchorCell);
            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || monster.Area != from)
                    continue;

                float angle = (float)(state.Rng.NextDouble() * Math.PI * 2d);
                float radius = 1f + (float)state.Rng.NextDouble() * 2.5f;
                var candidate = new Vector3f(
                    anchor.X + MathF.Cos(angle) * radius,
                    anchor.Y + MathF.Sin(angle) * radius,
                    0f);
                monster.Area = to;
                monster.Position = ClampToAreaWalkable(candidate, anchor, to);
                monster.ActivatesAtUtc = now.AddSeconds(EncircleTelegraphSeconds);
                monster.NextContactAtUtc = monster.ActivatesAtUtc;
                monster.ChaseTargetPlayerId = 0;
                moved.Add(monster.ToMonsterRuntimeInfo());
            }
        }

        return moved;
    }

    /// <summary>개봉 소음: 같은 구역 반경 안 잔상이 개봉자를 새 추적 목표로 삼는다.</summary>
    public void AttractSwarm(long matchingId, long playerId)
    {
        // SB 클론: 개봉은 몹을 부르지 않는다 — 개봉의 리스크는 다른 플레이어다.
        if (CampModeEnabled)
            return;
        if (!_matches.TryGetValue(matchingId, out var state))
            return;
        lock (state.SyncRoot)
        {
            bool found = false;
            var opener = default(SpotArenaPlayerSpatial);
            foreach (var participant in state.LastParticipants)
            {
                if (participant.PlayerId != playerId)
                    continue;
                found = true;
                opener = participant;
                break;
            }

            if (!found)
                return;

            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || monster.Area != opener.Area)
                    continue;
                float dx = monster.Position.X - opener.Position.X;
                float dy = monster.Position.Y - opener.Position.Y;
                if (dx * dx + dy * dy > ExploreAttractRadius * ExploreAttractRadius)
                    continue;
                monster.ChaseTargetPlayerId = playerId;
            }
        }
    }

    /// <summary>
    ///     봇 지시: 잔상 무리가 가까우면 반대쪽으로 이탈하고, 아니면 현재 구역 안을 배회한다.
    ///     M1의 최소 행동 — 경제(개봉·정예 사냥) 참여는 후속 증분에서 붙인다.
    /// </summary>
    public SpotArenaBotDirective GetBotDirective(long matchingId, long botPlayerId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SpotArenaBotDirective.None;

        lock (state.SyncRoot)
        {
            bool botFound = false;
            var bot = default(SpotArenaPlayerSpatial);
            foreach (var participant in state.LastParticipants)
            {
                if (participant.PlayerId != botPlayerId)
                    continue;
                botFound = true;
                bot = participant;
                break;
            }

            if (!botFound)
                return SpotArenaBotDirective.None;

            DateTime now = _utcNow();
            float threatX = 0f, threatY = 0f;
            int threatCount = 0;
            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || now < monster.ActivatesAtUtc || monster.Area != bot.Area)
                    continue;
                // 캠프 모드: 잠든 몹은 위험이 아니다 — 깨어난(어그로) 몹만 피한다.
                if (CampModeEnabled && !monster.Aggro)
                    continue;
                float dx = monster.Position.X - bot.Position.X;
                float dy = monster.Position.Y - bot.Position.Y;
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
                float awayX = bot.Position.X - centroidX;
                float awayY = bot.Position.Y - centroidY;
                float length = MathF.Sqrt(awayX * awayX + awayY * awayY);
                if (length < 0.01f)
                {
                    awayX = 1f;
                    awayY = 0f;
                    length = 1f;
                }

                destination = new Vector3f(
                    bot.Position.X + awayX / length * BotFleeDistance,
                    bot.Position.Y + awayY / length * BotFleeDistance,
                    0f);
                mode = SpotArenaBotMode.Return;
            }
            else
            {
                float angle = (float)(state.Rng.NextDouble() * Math.PI * 2d);
                destination = new Vector3f(
                    bot.Position.X + MathF.Cos(angle) * BotRoamDistance,
                    bot.Position.Y + MathF.Sin(angle) * BotRoamDistance,
                    0f);
                mode = SpotArenaBotMode.Escort;
            }

            destination = ClampToAreaWalkable(destination, bot.Position, bot.Area);
            return new SpotArenaBotDirective(
                mode,
                bot.Area,
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
                .Select(monster => monster.ToMonsterRuntimeInfo())
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
                    monster.Area,
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
            return new SwarmArenaSummary(
                state.HitsTaken,
                state.Kills,
                state.PatternHits.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
        }
    }

    public void RemoveMatching(long matchingId) => _matches.TryRemove(matchingId, out _);

    /// <summary>
    ///     구역 캠프 충원: 최초 진입 시 캠프를 세우고, 전멸한 캠프는 대기 후 되살린다.
    ///     몹은 앵커에 잠든 채 서 있다 — 방은 조용하고, 위험은 선택이다.
    /// </summary>
    private void EnsureAreaCamps(MatchState state, AreaType area, DateTime now, SwarmArenaTickResult result)
    {
        var (densityCap, _) = GetAreaProfile(area);
        if (densityCap <= 0)
            return;
        if (IsAreaClosedResolver?.Invoke(state.MatchingId, area) == true)
            return;

        if (state.CampInitializedAreas.Add(area))
        {
            for (int campIndex = 0; campIndex < CampsPerArea; campIndex++)
                SpawnCamp(state, area, campIndex, now, result);
            return;
        }

        for (int campIndex = 0; campIndex < CampsPerArea; campIndex++)
        {
            bool anyAlive = false;
            foreach (var monster in state.Monsters.Values)
            {
                if (monster.Alive && monster.Area == area && monster.CampIndex == campIndex)
                {
                    anyAlive = true;
                    break;
                }
            }

            if (anyAlive)
            {
                state.CampRespawnAtUtc.Remove((area, campIndex));
                continue;
            }

            if (!state.CampRespawnAtUtc.TryGetValue((area, campIndex), out var respawnAtUtc))
            {
                state.CampRespawnAtUtc[(area, campIndex)] = now.AddSeconds(CampRespawnSeconds);
                continue;
            }

            if (now < respawnAtUtc)
                continue;
            state.CampRespawnAtUtc.Remove((area, campIndex));
            SpawnCamp(state, area, campIndex, now, result);
        }
    }

    // 임시(비주얼 확인): 전 캠프를 고블린 2종만으로 스폰한다. 확인 끝나면 false로 복원.
    // 테스트는 정규 편성을 검증하므로 생성자에서 끈다.
    public static bool GoblinOnlySpawnForVisualCheck = false;

    /// <summary>
    ///     캠프 편성 (SB 배치 문법): 밴드·광장은 "다리 위 해골 3마리" — 전 캠프 해골 무리.
    ///     포드 방은 해골 무리 1캠프 + 다트 고블린 1기 + (탈주 고블린 | 볼러) 1기.
    /// </summary>
    private static SwarmMonsterKind[] GetCampComposition(MatchState state, AreaType area, int campIndex)
    {
        if (GoblinOnlySpawnForVisualCheck)
        {
            return campIndex switch
            {
                0 => [SwarmMonsterKind.DartGoblin, SwarmMonsterKind.DartGoblin, SwarmMonsterKind.DartGoblin],
                1 => [SwarmMonsterKind.DartGoblin],
                _ => [SwarmMonsterKind.RunawayGoblin]
            };
        }

        if (!StartRooms.Contains(area) || campIndex == 0)
            return [SwarmMonsterKind.Skeleton, SwarmMonsterKind.Skeleton, SwarmMonsterKind.Skeleton];
        if (campIndex == 1)
            return [SwarmMonsterKind.DartGoblin];
        return state.Rng.NextDouble() < 0.5d
            ? [SwarmMonsterKind.RunawayGoblin]
            : [SwarmMonsterKind.Bowler];
    }

    private static void SpawnCamp(
        MatchState state, AreaType area, int campIndex, DateTime now, SwarmArenaTickResult result)
    {
        var center = BotPlayerManager.CellToWorldPosition(
            MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));
        float campAngle = (float)(campIndex * Math.PI * 2d / CampsPerArea) +
                          (float)(state.Rng.NextDouble() * 0.6d - 0.3d);
        var campAnchor = ClampToAreaWalkable(new Vector3f(
            center.X + MathF.Cos(campAngle) * CampAnchorRadius,
            center.Y + MathF.Sin(campAngle) * CampAnchorRadius,
            0f), center, area);

        // 캠프별 오브 색 유지 — 처치 보상 색이 캠프 단위로 읽힌다.
        var pattern = (SwarmPattern)(campIndex % 3);
        var kinds = GetCampComposition(state, area, campIndex);
        for (int index = 0; index < kinds.Length; index++)
        {
            float angle = (float)(index * Math.PI * 2d / kinds.Length) +
                          (float)(state.Rng.NextDouble() * 0.5d - 0.25d);
            var position = ClampToAreaWalkable(new Vector3f(
                campAnchor.X + MathF.Cos(angle) * CampScatterRadius,
                campAnchor.Y + MathF.Sin(angle) * CampScatterRadius,
                0f), campAnchor, area);

            var stats = GetKindStats(kinds[index]);
            int serial = state.NextSerial++;
            var monster = new MonsterRuntime
            {
                MonsterId = FirstMonsterId + serial,
                CombatTargetId = FirstCombatTargetId - serial,
                Pattern = pattern,
                Area = area,
                Position = position,
                Health = stats.MaxHp,
                Alive = true,
                ActivatesAtUtc = now,
                NextContactAtUtc = now,
                ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
                SummonStoneReward = stats.StoneReward,
                ContactDamageValue = stats.OrbDamage,
                Kind = kinds[index],
                MaxHealthValue = stats.MaxHp,
                AttackRangeValue = stats.AttackRange,
                AttackCooldownValue = stats.AttackCooldownSeconds,
                CampIndex = campIndex,
                AnchorX = position.X,
                AnchorY = position.Y
            };
            state.Monsters[monster.MonsterId] = monster;
            result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
        }
    }

    /// <summary>캠프 몹 이동: 잠듦 → (근접·피격·접촉) 어그로 → 리쉬 안 추격 → 이탈 시 앵커 귀환.</summary>
    private static void UpdateCampMonsterMovement(
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        double deltaSeconds)
    {
        if (!monster.Aggro)
        {
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.Area != monster.Area)
                    continue;
                float aggroDx = participant.Position.X - monster.Position.X;
                float aggroDy = participant.Position.Y - monster.Position.Y;
                if (aggroDx * aggroDx + aggroDy * aggroDy > CampAggroRadius * CampAggroRadius)
                    continue;
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = participant.PlayerId;
                break;
            }

            if (!monster.Aggro)
                return;
        }

        // 추격 대상: 어그로 대상 우선, 구역을 떠났으면 같은 구역 최근접.
        bool found = false;
        var target = default(SpotArenaPlayerSpatial);
        float nearestSquared = float.MaxValue;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
                continue;
            if (participant.PlayerId == monster.ChaseTargetPlayerId)
            {
                target = participant;
                found = true;
                break;
            }

            float dx = participant.Position.X - monster.Position.X;
            float dy = participant.Position.Y - monster.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < nearestSquared)
            {
                nearestSquared = distanceSquared;
                target = participant;
                found = true;
            }
        }

        // 대상이 없거나 리쉬(앵커 기준) 밖이면 귀환 — 도주는 언제나 성립한다.
        bool returnToAnchor = !found;
        if (found)
        {
            float leashDx = target.Position.X - monster.AnchorX;
            float leashDy = target.Position.Y - monster.AnchorY;
            returnToAnchor = leashDx * leashDx + leashDy * leashDy > CampLeashRadius * CampLeashRadius;
        }

        if (returnToAnchor)
        {
            var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
            float homeDx = anchor.X - monster.Position.X;
            float homeDy = anchor.Y - monster.Position.Y;
            if (homeDx * homeDx + homeDy * homeDy <= CampReturnArriveDistance * CampReturnArriveDistance)
            {
                monster.Aggro = false;
                monster.ChaseTargetPlayerId = 0;
                return;
            }

            MoveTowardPlayer(monster, anchor, deltaSeconds);
            return;
        }

        MoveTowardPlayer(monster, target.Position, deltaSeconds);
    }

    private void SpawnDueParticipantPattern(
        MatchState state,
        SpotArenaPlayerSpatial participant,
        DateTime now,
        SwarmArenaTickResult result)
    {
        var (densityCap, intervalSeconds) = GetAreaProfile(participant.Area);
        if (densityCap <= 0)
            return;

        // 폐쇄 구역은 신규 스폰 정지 — 잔존 몹은 EvacuateArea가 다음 구역으로 밀어낸다.
        if (IsAreaClosedResolver?.Invoke(state.MatchingId, participant.Area) == true)
            return;

        int escalationStage = GetEscalationStage((now - state.StartsAtUtc).TotalSeconds);
        if (escalationStage == 1)
        {
            densityCap += 2;
            intervalSeconds *= 0.85d;
        }
        else if (escalationStage >= 2)
        {
            densityCap += 4;
            intervalSeconds *= 0.7d;
        }

        if (!state.PatternSchedules.TryGetValue(participant.PlayerId, out var schedule))
        {
            // 시작방의 선지급 무리는 거의 즉시 — 첫 전투가 개전과 함께 시작된다.
            double firstDelay = StartRooms.Contains(participant.Area)
                ? StartRoomFirstPatternDelaySeconds
                : FirstPatternDelaySeconds;
            schedule = new PatternSchedule
            {
                NextPatternAtUtc = now.AddSeconds(firstDelay)
            };
            state.PatternSchedules[participant.PlayerId] = schedule;
        }

        if (now < schedule.NextPatternAtUtc)
            return;

        bool inStartRoom = StartRooms.Contains(participant.Area);
        if (inStartRoom && schedule.StartRoomPacksSpawned >= StartRoomPackLimit)
            return;

        schedule.NextPatternAtUtc = now.AddSeconds(intervalSeconds);
        if (inStartRoom)
            schedule.StartRoomPacksSpawned++;

        int aliveInArea = state.Monsters.Values.Count(monster =>
            monster.Alive && monster.Area == participant.Area);
        if (aliveInArea >= densityCap)
            return;

        int budget = densityCap - aliveInArea;
        var pattern = schedule.NextPattern;
        schedule.NextPattern = (SwarmPattern)(((int)pattern + 1) % 3);
        switch (pattern)
        {
            case SwarmPattern.Ring:
                SpawnRing(state, participant, now, Math.Min(budget, RingSpawnCount), result);
                break;
            case SwarmPattern.Rush:
                SpawnRush(state, participant, now, Math.Min(budget, RushSpawnCount), result);
                break;
            default:
                SpawnEncircle(state, participant, now, Math.Min(budget, EncircleSpawnCount), result);
                break;
        }
    }

    private static void SpawnRing(
        MatchState state,
        SpotArenaPlayerSpatial anchor,
        DateTime now,
        int count,
        SwarmArenaTickResult result)
    {
        for (int index = 0; index < count; index++)
        {
            float angle = (float)(index * Math.PI * 2d / count) +
                          (float)(state.Rng.NextDouble() * 0.4d - 0.2d);
            var position = new Vector3f(
                anchor.Position.X + MathF.Cos(angle) * RingRadius,
                anchor.Position.Y + MathF.Sin(angle) * RingRadius,
                0f);
            SpawnMonster(state, anchor, position, now, RingTelegraphSeconds, SwarmPattern.Ring, result);
        }
    }

    private static void SpawnRush(
        MatchState state,
        SpotArenaPlayerSpatial anchor,
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
                anchor.Position.X + MathF.Cos(direction) * (RushDistance + depth) + lateralX * lateral,
                anchor.Position.Y + MathF.Sin(direction) * (RushDistance + depth) + lateralY * lateral,
                0f);
            SpawnMonster(state, anchor, position, now, RushTelegraphSeconds, SwarmPattern.Rush, result);
        }
    }

    private static void SpawnEncircle(
        MatchState state,
        SpotArenaPlayerSpatial anchor,
        DateTime now,
        int count,
        SwarmArenaTickResult result)
    {
        for (int index = 0; index < count; index++)
        {
            float angle = (float)(index * Math.PI * 2d / count);
            var position = new Vector3f(
                anchor.Position.X + MathF.Cos(angle) * EncircleRadius,
                anchor.Position.Y + MathF.Sin(angle) * EncircleRadius,
                0f);
            SpawnMonster(state, anchor, position, now, EncircleTelegraphSeconds, SwarmPattern.Encircle, result);
        }
    }

    private static void SpawnMonster(
        MatchState state,
        SpotArenaPlayerSpatial anchor,
        Vector3f position,
        DateTime now,
        float telegraphSeconds,
        SwarmPattern pattern,
        SwarmArenaTickResult result)
    {
        // 패턴 반경이 방 크기를 넘으면 참가자 쪽으로 끌어당겨 같은 구역 안에 스폰한다.
        // 구역 프로파일(시작방 저위험 등)이 옆 구역으로 새지 않게 하는 규칙.
        position = ClampToAreaWalkable(position, anchor.Position, anchor.Area);

        int serial = state.NextSerial++;
        var monster = new MonsterRuntime
        {
            MonsterId = FirstMonsterId + serial,
            CombatTargetId = FirstCombatTargetId - serial,
            Pattern = pattern,
            Area = anchor.Area,
            Position = position,
            Health = MonsterMaxHealth,
            Alive = true,
            ActivatesAtUtc = now.AddSeconds(telegraphSeconds),
            NextContactAtUtc = now,
            ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
            // 매 킬 1석: 시작방 선지급 10마리 = 방 스팟 2개(10석)를 정확히 커버한다.
            // 스팟이 유한 소진형이라 드롭률 인상은 스노우볼이 아니라 페이스 조절이다.
            SummonStoneReward = 1,
            ContactDamageValue = StartRooms.Contains(anchor.Area)
                ? StartRoomContactDamage
                : ContactDamage
        };
        state.Monsters[monster.MonsterId] = monster;
        result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
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

    private static bool IsWalkableInArea(Vector3f position, AreaType area)
    {
        Cell cell = MapCoordinateConverter.WorldToCell(MapId.School, position);
        return GameMapData.IsMoveablePosition(MapId.School, cell) &&
               GameMapData.GetCurrentArea(MapId.School, cell) == area;
    }

    private static Vector3f ClampToAreaWalkable(Vector3f position, Vector3f center, AreaType area)
    {
        if (IsWalkableInArea(position, area))
            return position;

        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            var candidate = new Vector3f(
                position.X + (center.X - position.X) * t,
                position.Y + (center.Y - position.Y) * t,
                0f);
            if (IsWalkableInArea(candidate, area))
                return candidate;
        }

        return center;
    }

    private static Vector3f ClampToWalkable(Vector3f position, Vector3f center)
    {
        if (GameMapData.IsMoveablePosition(
                MapId.School, MapCoordinateConverter.WorldToCell(MapId.School, position)))
            return position;

        // 스폰 위치가 보행 불가면 기준점 쪽으로 당기며 첫 보행 가능 지점을 찾는다.
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

    private sealed class PatternSchedule
    {
        public DateTime NextPatternAtUtc { get; set; }
        public SwarmPattern NextPattern { get; set; } = SwarmPattern.Ring;
        public int StartRoomPacksSpawned { get; set; }
    }

    private sealed class MatchState
    {
        public object SyncRoot { get; } = new();
        public long MatchingId { get; init; }
        public long HumanPlayerId { get; init; }
        public DateTime StartsAtUtc { get; init; }
        public DateTime LastTickAtUtc { get; set; }
        public Dictionary<int, MonsterRuntime> Monsters { get; } = new();
        public Dictionary<long, PatternSchedule> PatternSchedules { get; } = new();
        public Dictionary<long, DateTime> ContactImmuneUntilUtc { get; } = new();
        public Random Rng { get; init; } = new();
        public int NextSerial { get; set; }
        public bool NextMonsterGrantsSummonStone { get; set; } = true;
        public SpotArenaPlayerSpatial[] LastParticipants { get; set; } = [];
        public int HitsTaken { get; set; }
        public int Kills { get; set; }
        public Dictionary<SwarmPattern, int> PatternHits { get; } = new();
        public HashSet<AreaType> CampInitializedAreas { get; } = new();
        public Dictionary<(AreaType Area, int CampIndex), DateTime> CampRespawnAtUtc { get; } = new();
    }

    private sealed class MonsterRuntime
    {
        public int MonsterId { get; init; }
        public long CombatTargetId { get; init; }
        public SwarmPattern Pattern { get; init; }
        public AreaType Area { get; set; }
        public Vector3f Position { get; set; } = new(0f, 0f, 0f);
        public int Health { get; set; }
        public bool Alive { get; set; }
        public DateTime ActivatesAtUtc { get; set; }
        public DateTime NextContactAtUtc { get; set; }
        public DateTime DiedAtUtc { get; set; }
        public float ScatterAngle { get; init; }
        public int SummonStoneReward { get; init; }
        public int ContactDamageValue { get; init; }
        public long ChaseTargetPlayerId { get; set; }

        // 몬스터 4종: 종별 스탯은 스폰 시 박제된다. 레거시 스폰 경로는 기본값(해골 상당)을 쓴다.
        public SwarmMonsterKind Kind { get; init; } = SwarmMonsterKind.Skeleton;
        public int MaxHealthValue { get; init; } = MonsterMaxHealth;
        public float AttackRangeValue { get; init; } = ContactRange;
        public float AttackCooldownValue { get; init; } = ContactCooldownSeconds;

        // 캠프 모드: 소속 캠프와 제자리(앵커), 어그로 상태.
        public int CampIndex { get; set; } = -1;
        public float AnchorX { get; set; }
        public float AnchorY { get; set; }
        public bool Aggro { get; set; }

        public MonsterRuntimeInfo ToMonsterRuntimeInfo() => new()
        {
            MonsterId = MonsterId,
            AreaType = Area,
            PositionX = Position.X,
            PositionY = Position.Y,
            MaxHealth = MaxHealthValue,
            CurrentHealth = Health,
            IsAlive = Alive,
            RewardItemId = Pattern switch
            {
                SwarmPattern.Ring => 107000010,
                SwarmPattern.Rush => 107000020,
                _ => 107000030
            },
            IsCore = false,
            SummonStoneReward = SummonStoneReward,
            ChaseTargetPlayerId = ChaseTargetPlayerId
        };
    }
}

public enum SwarmPattern
{
    Ring = 0,
    Rush = 1,
    Encircle = 2
}

/// <summary>#219 SB 몬스터 4종. 보스(골렘·베이비 드래곤)는 M4 몫.</summary>
public enum SwarmMonsterKind
{
    Skeleton = 0,
    DartGoblin = 1,
    RunawayGoblin = 2,
    Bowler = 3
}

public sealed class SwarmArenaTickResult
{
    public List<SpotArenaPlayerDamage> PlayerDamage { get; } = new();
    public List<MonsterRuntimeInfo> SpawnedMonsters { get; } = new();
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
    int HitsTaken,
    int Kills,
    IReadOnlyDictionary<string, int> PatternHits)
{
    public static SwarmArenaSummary Empty => new(0, 0, new Dictionary<string, int>());
}
