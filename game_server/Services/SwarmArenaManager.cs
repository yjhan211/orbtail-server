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
    // #219 초반 템포 하향 (2026-08-09): 시작 스쿼드가 오브 1개뿐이라 20(2방)도 캠프 하나에
    // 14초가 걸렸다. 해골 = T1 한 방(12) — 초반 파밍이 사격 몇 번으로 끝나야 SB "코인 몹"
    // 감각이 산다.
    public const int MonsterMaxHealth = 12;

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

    // PvP 발당 데미지 배율 (#223 밸런싱): 전 발 적용(#222) 후 TTK가 너무 짧아
    // 매치가 2:47에 전멸로 끝났다(매치 2401 — 첫 킬 42초, 사망 7건 전부 PvP).
    // 몬스터전은 그대로 두고 PvP만 눌러 4분 타이머(잼 판정)까지 생존자가 남게 한다.
    // 빈손 오염(데미지 ×17.5)도 이 배율을 자동으로 따른다.
    public const float PvpDamageScale = 0.65f;

    // 개봉 소음 유인 반경: 채집을 시작하면 같은 구역 이 반경의 잔상이 개봉자에게 몰린다.
    public const float ExploreAttractRadius = 14f;

    // #219 SB 클론 M1: 몹 개시권을 플레이어에게. 몹은 캠프에 고정되고, 근접하거나
    // 맞았을 때만 리쉬 안에서 반격 추격하며, 리쉬를 벗어나면 캠프로 돌아가 잠든다.
    // 방은 조용하고 위험은 선택이다 — 켜면 추적 스웜 디렉터(패턴 스폰)는 쉰다.
    public static readonly bool CampModeEnabled = true;

    // #229 4단계 점유 구역 지속 웨이브: 공급지가 돌아다니던 #226 E 모델(무리 2회 → 이전)을
    // 퇴역하고, 플레이어가 서 있는 열린 구역마다 목표 개체 수를 유지한다. 오브는 쉬지 않고
    // 쏘고, 폐쇄가 진행될수록 웨이브가 두꺼워진다 — 몹 체력을 시간에 따라 올려(#226 E는
    // 고정이었다) 공격 강화가 "같은 웨이브를 더 빨리"가 아니라 "더 두꺼운 웨이브를 같은
    // 속도로" 치우는 것으로 체감되게 한다. 서버 부하는 전역 상한 48로 잡는다.
    public static bool RegionSupplyModeEnabled = true;
    // 폐쇄 단계별 웨이브 곡선 (#229 4단계 P0 확정). 접촉 피해는 원시 스탯이라
    // 참가자 피해 절반 배율(SwarmMonsterDamageTakenMultiplier)을 지나 1/1/1/1/2로 들어간다.
    private static readonly (double UntilSeconds, int ZoneTarget, int NormalHp, int ContactDamage,
        int CoreHp, int StoneBudget)[] SupplyPhases =
    [
        (100d, 9, 12, 1, 48, 10), // 0:00~1:40 폐쇄 전
        (150d, 12, 15, 1, 60, 11), // 1:40~2:30 1차
        (200d, 15, 18, 2, 72, 12), // 2:30~3:20 2차
        (250d, 18, 24, 2, 96, 13), // 3:20~4:10 3차
        (double.MaxValue, 18, 30, 3, 120, 13) // 4:10~5:00 최종 수렴
    ];

    private static int GetSupplyPhaseIndex(double elapsedSeconds)
    {
        for (int index = 0; index < SupplyPhases.Length; index++)
        {
            if (elapsedSeconds < SupplyPhases[index].UntilSeconds)
                return index;
        }

        return SupplyPhases.Length - 1;
    }

    // 목표 수 아래면 1.5초마다 2마리씩 채운다 — 한 번에 몰아 넣으면 화면이 터지고,
    // 한 마리씩이면 고DPS 플레이어의 공백이 길어진다.
    private const double SupplyTopUpIntervalSeconds = 1.5d;
    private const int SupplyTopUpCount = 2;
    // 구역 전멸 뒤 휴지: 짧은 수면 창이 성장의 보상이다 (#229 완료 조건 2).
    private const double SupplyWipeRestSeconds = 4d;
    // 플레이어 2.5m 안의 앵커에는 즉시 생성하지 않는다 — 전 앵커가 막히면 1초 뒤 재검사.
    private const float SupplySafeSpawnDistance = 2.5f;
    // 화면 밖 등장 (#229): 이 거리 밖 앵커를 우선 고른다. 전부 가까우면 안전 이격만 지킨다.
    private const float SupplyOffscreenDistance = 7f;
    private const double SupplyBlockedRetrySeconds = 1d;
    private const int SupplyCoreStoneReward = 3;

    // 추격 대상 유지 창 (#229): 이 시간이 지나야 최근접을 다시 고른다.
    private const double SupplyTargetHoldSeconds = 1d;

    // 전역 활성 잔상 상한 (#229 4단계): 인원 무관 48마리. 보스는 고정 콘텐츠라 제외한다.
    private const int SupplyGlobalAliveCap = 48;
    private const float SupplyTelegraphSeconds = 1f;
    private const float SupplyScatterRadius = 1.6f;
    // 원거리 종(다트·볼러)은 사거리의 이 비율에서 멈춰 쏜다 — 근접 종만 몸으로 파고든다.
    private const float RangedHoldRangeRatio = 0.8f;

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

    public static (int MaxHp, int OrbDamage, float AttackRange, float AttackCooldownSeconds, int StoneReward,
        int HeartReward, int BootsReward, int KeyReward)
        GetKindStats(SwarmMonsterKind kind) => kind switch
        {
            // 피통 = 시작 T1 오브(발당 12) 발수 정렬: 다트 2방 · 볼러 4방 (#219 초반 템포)
            // 탈주 120 (#222 연사화 후 상향): 스쿼드 DPS ~50에 60은 1초 컷 — 미니보스 체급 복원.
            // 피통은 클라 종 식별자이기도 하다 — EmotionAfterimageMonsterDisplay 스위치와 동기 필수.
            // 하트 (#222 M4): 고위험 몹(탈주·볼러)만 확정 1 — 즉시 회복 픽업의 유일 공급처.
            // 부츠·열쇠 (#222 M4): 부츠 = 다트(저보상 몹의 아이덴티티), 열쇠 = 탈주(미니보스 확정 드롭).
            SwarmMonsterKind.DartGoblin => (18, 2, 5f, 2f, 1, 0, 1, 0),
            SwarmMonsterKind.RunawayGoblin => (120, 5, ContactRange, 1.2f, 4, 1, 0, 1),
            SwarmMonsterKind.Bowler => (48, 2, 4.5f, 2.5f, 4, 1, 0, 0),
            // 보스 (#223, SB 드롭 = 코인 11 + 젬 7): 피통은 클라 종 식별자 — 기존 값과 겹치면 안 된다.
            // 전원 제자리 고정 포대 — 파도 T3급 사거리(Config 공유 = 클라 범위 링)로 투사체를 던진다.
            // 골렘 = 광역 강타(볼러 스플래시 공유), 트리 자이언트 = 열쇠 확정 드롭.
            // 데미지는 참가자 피해 절반 배율(0.5) 통과 후가 실효 — 골렘 12·드래곤 6·트리 9.
            // T1 오브(24)가 골렘 두 방에 깨진다: 링 안 눌러앉기가 실제로 비싸야 위협이다 (#223).
            SwarmMonsterKind.Golem => (240, 24, Config.SWARM_BOSS_ATTACK_RANGE, 2.8f, 11, 0, 0, 0),
            SwarmMonsterKind.BabyDragon => (200, 12, Config.SWARM_BOSS_ATTACK_RANGE, 2f, 11, 0, 0, 0),
            SwarmMonsterKind.TreeGiant => (260, 18, Config.SWARM_BOSS_ATTACK_RANGE, 2.2f, 8, 0, 0, 1),
            _ => (MonsterMaxHealth, 1, ContactRange, ContactCooldownSeconds, 1, 0, 0, 0)
        };

    /// <summary>보스 판별 (#223): 고정 포대·리스폰 없음·타원 판정 공유의 스위치.</summary>
    public static bool IsBossKind(SwarmMonsterKind kind) =>
        kind is SwarmMonsterKind.Golem or SwarmMonsterKind.BabyDragon or SwarmMonsterKind.TreeGiant;

    /// <summary>클라 보스 연출(투사체) 분기용 — 스웜 공격 VFX 브로드캐스트가 묻는다.</summary>
    public bool IsBossMonster(long matchingId, int monsterId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            return state.Monsters.TryGetValue(monsterId, out var monster) && IsBossKind(monster.Kind);
        }
    }

    /// <summary>
    ///     보스 상주 구역 (#223): 중간 지대 캠프 0번이 보스 단독 캠프가 된다.
    ///     School 맵의 실존 중간 지대는 3곳뿐 — 북 밴드(정크장)·남 밴드(회랑)·운동장.
    ///     트리 자이언트(열쇠)는 운동장 — 광산(150초 개장)과 같은 무대의 선주민 수호자.
    /// </summary>
    private static bool TryGetBossKind(AreaType area, out SwarmMonsterKind kind)
    {
        switch (area)
        {
            case AreaType.Junkyard:
                kind = SwarmMonsterKind.Golem;
                return true;
            case AreaType.Corridor:
                kind = SwarmMonsterKind.BabyDragon;
                return true;
            case AreaType.Ground:
                kind = SwarmMonsterKind.TreeGiant;
                return true;
            default:
                kind = SwarmMonsterKind.Skeleton;
                return false;
        }
    }

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

            if (RegionSupplyModeEnabled)
            {
                ProcessRegionSupply(state, now, result);
            }
            else if (CampModeEnabled)
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

                if (RegionSupplyModeEnabled)
                {
                    UpdateSupplyMonsterMovement(monster, state.LastParticipants, now, moveDeltaSeconds);
                }
                else if (CampModeEnabled)
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
                // 보스 판정은 타원(dy×2) (#223): 범위 링 스프라이트가 아이소 타원이라
                // 원형 판정이면 세로로 링 밖까지 맞는다 — PvP와 같은 규칙으로 표시 = 판정.
                float verticalScale = IsBossKind(monster.Kind) ? 2f : 1f;
                foreach (var participant in state.LastParticipants)
                {
                    if (participant.Area != monster.Area)
                        continue;
                    float dx = monster.Position.X - participant.Position.X;
                    float dy = (monster.Position.Y - participant.Position.Y) * verticalScale;
                    if (dx * dx + dy * dy > attackRange * attackRange)
                        continue;
                    if (state.ContactImmuneUntilUtc.TryGetValue(participant.PlayerId, out var immuneUntil) &&
                        now < immuneUntil)
                        continue;

                    monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
                    state.ContactImmuneUntilUtc[participant.PlayerId] =
                        now.AddSeconds(ContactImmunitySeconds);
                    if (CampModeEnabled || RegionSupplyModeEnabled)
                    {
                        // 부딪힘도 개전이다 — 맞은 캠프·공급 몹이 깨어난다.
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
                    // 골렘(#223)도 공유 — 광역 강타가 보스 접근전의 특수공격 근사다.
                    if (monster.Kind is SwarmMonsterKind.Bowler or SwarmMonsterKind.Golem)
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

                    // 보스 (#223): 범위 안 전원 동시 타격 — 고정 포대는 한 명씩 고르지 않는다.
                    if (!IsBossKind(monster.Kind))
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

            if (RegionSupplyModeEnabled)
            {
                // 반격 개전: 맞은 몹과 같은 구역 무리 전체가 함께 깨어난다 — 무리는 한 덩어리다.
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = attackerPlayerId;
                foreach (var mate in state.Monsters.Values)
                {
                    if (!mate.Alive || mate.Aggro || mate.Area != monster.Area)
                        continue;
                    mate.Aggro = true;
                    mate.ChaseTargetPlayerId = attackerPlayerId;
                }
            }
            else if (CampModeEnabled)
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

            return new SwarmArenaDamageResult(
                true, killed, monster.MonsterId, monster.ToMonsterRuntimeInfo(),
                monster.HeartReward, monster.BootsReward, monster.KeyReward, monster.Kind);
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
                // 지역 공급 (#226 단계 B): 이주 후 복귀 앵커도 새 자리로 — 옛 구역으로 걸어가지 않는다.
                monster.AnchorX = monster.Position.X;
                monster.AnchorY = monster.Position.Y;
                monster.Aggro = false;
                moved.Add(monster.ToMonsterRuntimeInfo());
            }
        }

        return moved;
    }

    /// <summary>개봉 소음: 같은 구역 반경 안 잔상이 개봉자를 새 추적 목표로 삼는다.</summary>
    public void AttractSwarm(long matchingId, long playerId)
    {
        // SB 클론: 개봉은 몹을 부르지 않는다 — 개봉의 리스크는 다른 플레이어다.
        // 지역 공급 모드도 동일 — 몹은 찾아가는 자원이지 배달되는 압박이 아니다.
        if (CampModeEnabled || RegionSupplyModeEnabled)
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
                // 캠프·지역 공급 모드: 잠든 몹은 위험이 아니다 — 깨어난(어그로) 몹만 피한다.
                if ((CampModeEnabled || RegionSupplyModeEnabled) && !monster.Aggro)
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
            if (threatCount > 0)
            {
                // 도주 목적지 약속 (#226): 웨이브 몹이 상시 물어 피격 재계획이 계속 돌면
                // 목적지가 매번 새로 뽑혀 방향이 뒤집혔다(뚝뚝 끊기는 이동). 유효 시간 안이고
                // 아직 도착 전이면 같은 목적지를 유지한다.
                if (state.BotFleeCommitments.TryGetValue(botPlayerId, out var commitment) &&
                    (now - commitment.CommittedAtUtc).TotalSeconds < BotFleeCommitSeconds)
                {
                    float commitDx = commitment.Destination.X - bot.Position.X;
                    float commitDy = commitment.Destination.Y - bot.Position.Y;
                    if (commitDx * commitDx + commitDy * commitDy > 1f)
                    {
                        return new SpotArenaBotDirective(
                            SpotArenaBotMode.Return,
                            bot.Area,
                            MapCoordinateConverter.WorldToCell(MapId.School, commitment.Destination),
                            commitment.Destination);
                    }
                }

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

                // 구석 수렴 방지 (#223): 위협 반대가 벽이면 클램프가 제자리를 돌려줘
                // "몬스터 옆에 붙어 서 있는" 봇이 됐다 — 각도를 돌려가며 실제로 멀어지는
                // 후보를 찾고, 전부 막히면 구역 스폰 지점으로 물러난다.
                destination = ResolveThreatFleeDestination(
                    bot, awayX / length, awayY / length);
                state.BotFleeCommitments[botPlayerId] = (destination, now);
            }
            else
            {
                state.BotFleeCommitments.Remove(botPlayerId);
                // 지역 공급 파밍 유도는 여기(Return)가 아니라 봇 파이프라인의 사냥 단계가 맡는다 —
                // Return 지시는 개봉 채널 완료 로직을 단락시켜 봇이 채널을 문 채 얼었다 (2026-08-12).
                float angle = (float)(state.Rng.NextDouble() * Math.PI * 2d);
                destination = new Vector3f(
                    bot.Position.X + MathF.Cos(angle) * BotRoamDistance,
                    bot.Position.Y + MathF.Sin(angle) * BotRoamDistance,
                    0f);
                destination = ClampToAreaWalkable(destination, bot.Position, bot.Area);
            }

            var mode = threatCount > 0 ? SpotArenaBotMode.Return : SpotArenaBotMode.Escort;
            return new SpotArenaBotDirective(
                mode,
                bot.Area,
                MapCoordinateConverter.WorldToCell(MapId.School, destination),
                destination);
        }
    }

    // 위협 회피 목적지 최소 거리 — 클램프 후 이보다 가까우면 그 각도는 벽이다.
    private const float MinThreatFleeDistance = 2f;

    // 도주 목적지 약속 유효 시간 (#226): 이 시간 안에는 같은 도주 목적지를 반환한다.
    private const double BotFleeCommitSeconds = 2d;

    /// <summary>
    ///     위협 반대 방향부터 각도를 넓혀가며(±45°… 180°) 실제로 멀어지는 walkable 목적지를
    ///     찾는다 (#223 구석 수렴 방지). 전부 벽이면 구역 스폰 지점 — 몬스터 옆 정지는 없다.
    /// </summary>
    private static Vector3f ResolveThreatFleeDestination(
        SpotArenaPlayerSpatial bot, float directionX, float directionY)
    {
        ReadOnlySpan<float> angleOffsets = [0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f];
        foreach (float angleDegrees in angleOffsets)
        {
            float radians = angleDegrees * MathF.PI / 180f;
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            float rotatedX = directionX * cos - directionY * sin;
            float rotatedY = directionX * sin + directionY * cos;
            var candidate = ClampToAreaWalkable(new Vector3f(
                bot.Position.X + rotatedX * BotFleeDistance,
                bot.Position.Y + rotatedY * BotFleeDistance,
                0f), bot.Position, bot.Area);
            float dx = candidate.X - bot.Position.X;
            float dy = candidate.Y - bot.Position.Y;
            if (dx * dx + dy * dy >= MinThreatFleeDistance * MinThreatFleeDistance)
                return candidate;
        }

        return ClampToAreaWalkable(
            BotPlayerManager.CellToWorldPosition(
                MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, bot.Area)),
            bot.Position, bot.Area);
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

    /// <summary>착탄 지연 피해의 발사 연출용 — 전투 대상 id로 몬스터 id를 조회한다. 없으면 0.</summary>
    public int GetMonsterIdForCombatTarget(long matchingId, long combatTargetId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return 0;
        lock (state.SyncRoot)
        {
            var monster = state.Monsters.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId && candidate.Alive);
            return monster?.MonsterId ?? 0;
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

            // 보스는 리스폰하지 않는다 (#223): 11석+7잼 드롭이 45초마다 돌면 경제가 터진다.
            if (!StartRooms.Contains(area) && campIndex == 0 && TryGetBossKind(area, out _))
                continue;

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

        if (!StartRooms.Contains(area))
        {
            // 보스 구역 (#223): 캠프 0번 = 보스 단독. 나머지 캠프는 해골 무리 유지.
            if (campIndex == 0 && TryGetBossKind(area, out var bossKind))
                return [bossKind];
            return [SwarmMonsterKind.Skeleton, SwarmMonsterKind.Skeleton, SwarmMonsterKind.Skeleton];
        }

        if (campIndex == 0)
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
        Vector3f campAnchor;
        var customAnchorCell = GameMonsterCampData.GetAnchor(area, campIndex);
        if (customAnchorCell != null)
        {
            // 커스텀 앵커 (#219): monster_camp_anchor.csv가 지정한 셀. 지터 없이 고정 —
            // 저작한 위치가 곧 실배치다. walkable 클램프만 안전망으로 유지한다.
            campAnchor = ClampToAreaWalkable(
                BotPlayerManager.CellToWorldPosition(MapId.School, customAnchorCell), center, area);
        }
        else
        {
            float campAngle = (float)(campIndex * Math.PI * 2d / CampsPerArea) +
                              (float)(state.Rng.NextDouble() * 0.6d - 0.3d);
            campAnchor = ClampToAreaWalkable(new Vector3f(
                center.X + MathF.Cos(campAngle) * CampAnchorRadius,
                center.Y + MathF.Sin(campAngle) * CampAnchorRadius,
                0f), center, area);
        }

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
            // 첫 캠프 경제 (#223): 시작방 첫 캠프(해골 3)는 마리당 2석 — 클리어 즉시
            // 첫 상자(비용 5)가 열린다. 1석 몹 5마리 노가다(첫 소환 36초, 매치 2402)의 수리.
            int stoneReward = stats.StoneReward;
            if (campIndex == 0 && kinds[index] == SwarmMonsterKind.Skeleton && StartRooms.Contains(area))
                stoneReward = 2;
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
                SummonStoneReward = stoneReward,
                HeartReward = stats.HeartReward,
                BootsReward = stats.BootsReward,
                KeyReward = stats.KeyReward,
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
            // 보스 (#223): 링 안 = 개전 — 어그로 반경이 곧 사거리(타원 dy×2)라 범위 링이
            // 안전선으로 정직해진다. 일반 몹은 좁은 접근 반경(2.5) 유지.
            bool isBoss = IsBossKind(monster.Kind);
            float aggroRadius = isBoss ? monster.AttackRangeValue : CampAggroRadius;
            float aggroVerticalScale = isBoss ? 2f : 1f;
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.Area != monster.Area)
                    continue;
                float aggroDx = participant.Position.X - monster.Position.X;
                float aggroDy = (participant.Position.Y - monster.Position.Y) * aggroVerticalScale;
                if (aggroDx * aggroDx + aggroDy * aggroDy > aggroRadius * aggroRadius)
                    continue;
                monster.Aggro = true;
                monster.ChaseTargetPlayerId = participant.PlayerId;
                break;
            }

            if (!monster.Aggro)
                return;
        }

        // 보스는 고정 포대 (#223): 추격도 귀환도 없다 — 어그로만 켜지고 제자리에서 쏜다.
        if (IsBossKind(monster.Kind))
            return;

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

    /// <summary>
    ///     지역 공급 디렉터 (#226 단계 B): 시작 선물(시작 구역 일반 3, 1회) → 활성 파밍 구역
    ///     4→3→2곳(구역마다 일반 6 + 핵 1) → 4:00 이후 신규 스폰 중단. 무리 완전 처치 후
    ///     한 번만 증원하고, 두 무리째 소진되면 구역이 고갈되어 다른 안전 구역으로 옮긴다.
    /// </summary>
    private void ProcessRegionSupply(MatchState state, DateTime now, SwarmArenaTickResult result)
    {
        // 시작 선물 창(#226 E: 15초 침묵 후 무리 일괄 등장)은 퇴역했다 — 목표 수 유지 모델에서는
        // 0초부터 보충이 돌아 오브가 첫 틱부터 쏠 것을 갖는다 (#229 완료 조건 1).
        double elapsed = (now - state.StartsAtUtc).TotalSeconds;
        state.MaxParticipantCount = Math.Max(state.MaxParticipantCount, state.LastParticipants.Length);
        int phaseIndex = GetSupplyPhaseIndex(elapsed);
        var phase = SupplyPhases[phaseIndex];

        // 점유 = 살아있는 참가자가 서 있는 구역. 폐쇄 구역은 즉시 제외한다 — 신규 스폰이
        // 폐쇄 구역에 쌓이면 EvacuateArea가 옮기느라 상한만 갉아먹는다 (#229 완료 조건 4).
        var occupied = new HashSet<AreaType>();
        foreach (var participant in state.LastParticipants)
        {
            if (participant.Area == AreaType.None ||
                IsAreaClosedResolver?.Invoke(state.MatchingId, participant.Area) == true)
                continue;
            occupied.Add(participant.Area);
        }

        // 예산 회수 (#229): 비점유·폐쇄 구역은 공급 상태를 버린다. 잔존 몹은 그대로 두되
        // 보충이 끊기고, 다시 점유되면 휴지 없이 처음부터 채운다.
        foreach (var zone in state.SupplyZones.Keys.Where(zone => !occupied.Contains(zone)).ToList())
            state.SupplyZones.Remove(zone);

        // 전역 상한은 매 틱 새로 계산한다 — 여러 구역이 같은 틱에 채우면 합계가 넘칠 수 있다.
        int aliveGlobal = CountAliveGlobal(state);

        // 상한에 걸리면 뒤 구역이 굶는다 — 빈 구역부터 채워 공백을 고르게 나눈다.
        // 10인이 5개 구역에 흩어지면 목표 합(18×5)이 상한 48을 넘으므로 실제로 자주 걸린다.
        foreach (var zone in occupied.OrderBy(candidate => CountAliveInArea(state, candidate)))
        {
            if (aliveGlobal >= SupplyGlobalAliveCap)
                break;

            if (!state.SupplyZones.TryGetValue(zone, out var zoneState))
            {
                zoneState = new SupplyZoneState();
                state.SupplyZones[zone] = zoneState;
            }

            int aliveInZone = CountAliveInArea(state, zone);
            if (aliveInZone >= phase.ZoneTarget)
            {
                zoneState.NextTopUpAtUtc = null;
                continue;
            }

            // 전멸 휴지: 한 번이라도 채운 구역이 0이 되면 4초 뒤부터 보충을 재개한다.
            if (aliveInZone == 0 && zoneState.HasSpawned)
            {
                if (zoneState.WipeRestUntilUtc == null)
                {
                    zoneState.WipeRestUntilUtc = now.AddSeconds(SupplyWipeRestSeconds);
                    zoneState.NextTopUpAtUtc = null;
                }

                if (now < zoneState.WipeRestUntilUtc)
                    continue;
            }
            else if (aliveInZone > 0)
            {
                zoneState.WipeRestUntilUtc = null;
            }

            // 첫 보충은 즉시, 이후는 1.5초 간격.
            zoneState.NextTopUpAtUtc ??= now;
            if (now < zoneState.NextTopUpAtUtc)
                continue;

            // 핵은 구역당 1기 유지 — 죽으면 다음 보충에 다시 선다. 핵도 상한을 쓴다.
            bool includeCore = !HasAliveCore(state, zone) &&
                               aliveInZone < phase.ZoneTarget &&
                               aliveGlobal < SupplyGlobalAliveCap;
            int room = phase.ZoneTarget - aliveInZone - (includeCore ? 1 : 0);
            int want = Math.Min(SupplyTopUpCount, room);
            want = Math.Min(want, SupplyGlobalAliveCap - aliveGlobal - (includeCore ? 1 : 0));
            if (want <= 0 && !includeCore)
                continue;

            want = Math.Max(0, want);
            int spawned = SpawnSupplyMonsters(
                state, zone, want, includeCore, phaseIndex, now, result);
            if (spawned == 0)
            {
                // 전 앵커가 플레이어 2.5m 안 — 1초 뒤 재검사.
                zoneState.NextTopUpAtUtc = now.AddSeconds(SupplyBlockedRetrySeconds);
                continue;
            }

            aliveGlobal += spawned;
            zoneState.HasSpawned = true;
            zoneState.WipeRestUntilUtc = null;
            zoneState.NextTopUpAtUtc = now.AddSeconds(SupplyTopUpIntervalSeconds);
        }
    }

    /// <summary>전역 생존 몹 수 — 보스(고정 콘텐츠)는 공급 상한에서 제외한다.</summary>
    private static int CountAliveGlobal(MatchState state) =>
        state.Monsters.Values.Count(monster => monster.Alive && !IsBossKind(monster.Kind));

    /// <summary>구역에 살아있는 핵(탈주 고블린)이 있는지 — 핵은 구역당 1기만 유지한다.</summary>
    private static bool HasAliveCore(MatchState state, AreaType area) =>
        state.Monsters.Values.Any(monster =>
            monster.Alive && monster.Area == area && monster.Kind == SwarmMonsterKind.RunawayGoblin);

    private static int CountAliveInArea(MatchState state, AreaType area) =>
        state.Monsters.Values.Count(monster => monster.Alive && monster.Area == area);

    /// <summary>
    ///     공급 보충 스폰 (#229 4단계): 구역 앵커 3개(캠프 앵커 CSV 우선) 중 플레이어에게서
    ///     가장 먼 곳에 산개, 1초 예고 후 등장한다. 7m 밖 앵커를 우선 골라 화면 안에서 튀어나오지
    ///     않게 하고, 2.5m 안 앵커는 아예 제외한다 — 전 앵커가 막히면 0을 반환해 호출부가 1초 뒤
    ///     재검사한다. 잠든 채 서 있다 — 근접·피격·접촉이 개전이고, 구역 경계가 리쉬다.
    ///     HP·접촉 피해는 페이즈 곡선을 따르고, 소환석은 구역·페이즈 예산이 남아 있을 때만 붙는다.
    /// </summary>
    /// <returns>실제로 세운 마릿수.</returns>
    private static int SpawnSupplyMonsters(
        MatchState state, AreaType area, int normals, bool includeCore,
        int phaseIndex, DateTime now, SwarmArenaTickResult result)
    {
        var phase = SupplyPhases[phaseIndex];
        var budgetKey = (area, phaseIndex);
        if (!state.SupplyStoneBudget.ContainsKey(budgetKey))
            state.SupplyStoneBudget[budgetKey] = phase.StoneBudget;
        var center = BotPlayerManager.CellToWorldPosition(
            MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));
        var anchors = new List<Vector3f>(CampsPerArea);
        for (int anchorIndex = 0; anchorIndex < CampsPerArea; anchorIndex++)
        {
            var customAnchorCell = GameMonsterCampData.GetAnchor(area, anchorIndex);
            if (customAnchorCell != null)
            {
                anchors.Add(ClampToAreaWalkable(
                    BotPlayerManager.CellToWorldPosition(MapId.School, customAnchorCell), center,
                    area));
                continue;
            }

            // 절차 폴백: 방 중심 주변 120도 간격 배치.
            float anchorAngle = anchorIndex * 2.0944f;
            anchors.Add(ClampToAreaWalkable(new Vector3f(
                center.X + CampAnchorRadius * 0.6f * MathF.Cos(anchorAngle),
                center.Y + CampAnchorRadius * 0.6f * MathF.Sin(anchorAngle), 0f), center, area));
        }

        // 앵커별 최근접 플레이어 거리 — 안전 이격(2.5m) 미만은 제외하고, 화면 밖(7m)을 우선한다.
        var ranked = anchors
            .Select(anchor => (Anchor: anchor, Distance: NearestParticipantDistance(state, area, anchor)))
            .Where(entry => entry.Distance >= SupplySafeSpawnDistance)
            .OrderByDescending(entry => entry.Distance)
            .ToList();
        if (ranked.Count == 0)
            return 0;

        var offscreen = ranked.Where(entry => entry.Distance >= SupplyOffscreenDistance).ToList();
        var freeAnchors = (offscreen.Count > 0 ? offscreen : ranked)
            .Select(entry => entry.Anchor)
            .ToList();

        // 보충은 소수(2마리)라 앵커를 순회하며 흩는다 — 한 점에 뭉쳐 나오면 절단 한 번에 쓸린다.
        var spawnPlan = new List<(SwarmMonsterKind Kind, Vector3f Anchor)>(normals + 1);
        for (int index = 0; index < normals; index++)
            spawnPlan.Add((SwarmMonsterKind.Skeleton, freeAnchors[index % freeAnchors.Count]));

        if (includeCore)
            spawnPlan.Add((SwarmMonsterKind.RunawayGoblin, freeAnchors[^1]));

        int stoneTotal = 0;
        var pattern = (SwarmPattern)(state.NextSupplyPackOrdinal++ % 3);
        for (int index = 0; index < spawnPlan.Count; index++)
        {
            var packAnchor = spawnPlan[index].Anchor;
            float angle = (float)(index * Math.PI * 2d / spawnPlan.Count) +
                          (float)(state.Rng.NextDouble() * 0.5d - 0.25d);
            var position = ClampToAreaWalkable(new Vector3f(
                packAnchor.X + MathF.Cos(angle) * SupplyScatterRadius,
                packAnchor.Y + MathF.Sin(angle) * SupplyScatterRadius,
                0f), packAnchor, area);

            var kind = spawnPlan[index].Kind;
            var stats = GetKindStats(kind);
            bool isCore = kind == SwarmMonsterKind.RunawayGoblin;

            // 핵 보상은 일반 예산과 섞지 않고 별도 정산한다 (#229) — 구역·페이즈당 1기까지만
            // 석 3을 주고, 그 뒤로 다시 서는 핵은 몸만 나온다. 핵은 죽을 때마다 다시 서므로
            // 무제한으로 주면 한 구역에 눌러앉는 것이 최적해가 된다.
            // 일반은 구역·페이즈 예산이 남아 있을 때만 석이 붙고, 소진되면 몸만 계속 나온다.
            int stoneReward;
            if (isCore)
            {
                stoneReward = state.SupplyCoreRewarded.Add(budgetKey) ? SupplyCoreStoneReward : 0;
            }
            else if (state.SupplyStoneBudget[budgetKey] >= stats.StoneReward)
            {
                stoneReward = stats.StoneReward;
                state.SupplyStoneBudget[budgetKey] -= stats.StoneReward;
            }
            else
            {
                stoneReward = 0;
            }

            // 페이즈 곡선: 일반은 HP·접촉 피해를, 핵은 HP를 덮어쓴다 (#229 4단계).
            // 종 정체는 Kind가 들고 있으므로 피통을 바꿔도 클라 표시는 흔들리지 않는다.
            int maxHp = isCore ? phase.CoreHp : phase.NormalHp;
            int contactDamage = isCore ? stats.OrbDamage : phase.ContactDamage;

            stoneTotal += stoneReward;
            int serial = state.NextSerial++;
            var monster = new MonsterRuntime
            {
                MonsterId = FirstMonsterId + serial,
                CombatTargetId = FirstCombatTargetId - serial,
                Pattern = pattern,
                Area = area,
                Position = position,
                Health = maxHp,
                Alive = true,
                ActivatesAtUtc = now.AddSeconds(SupplyTelegraphSeconds),
                NextContactAtUtc = now,
                ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
                SummonStoneReward = stoneReward,
                HeartReward = stats.HeartReward,
                BootsReward = stats.BootsReward,
                KeyReward = stats.KeyReward,
                ContactDamageValue = contactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                AttackRangeValue = stats.AttackRange,
                AttackCooldownValue = stats.AttackCooldownSeconds,
                AnchorX = position.X,
                AnchorY = position.Y
            };
            state.Monsters[monster.MonsterId] = monster;
            result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
        }

        // 공급 계측 (#229): 공급지·페이즈·마릿수·석 보상을 매치 로그로 넘긴다.
        result.SupplyPackSpawns.Add(new SupplyPackSpawnInfo(
            area, phaseIndex, spawnPlan.Count, stoneTotal));
        return spawnPlan.Count;
    }

    /// <summary>같은 구역 참가자 중 앵커에서 가장 가까운 거리 — 아무도 없으면 무한대.</summary>
    private static float NearestParticipantDistance(MatchState state, AreaType area, Vector3f anchor)
    {
        float nearestSquared = float.MaxValue;
        foreach (var participant in state.LastParticipants)
        {
            if (participant.Area != area)
                continue;
            float dx = participant.Position.X - anchor.X;
            float dy = participant.Position.Y - anchor.Y;
            nearestSquared = Math.Min(nearestSquared, dx * dx + dy * dy);
        }

        return nearestSquared == float.MaxValue ? float.MaxValue : MathF.Sqrt(nearestSquared);
    }


    /// <summary>
    ///     공급 몹 이동 (#226 단계 B): 잠듦 → (근접·피격·접촉) 개전 → 같은 구역 안에서만 추격.
    ///     대상이 구역 경계(문)를 넘으면 앵커로 복귀해 다시 잠든다 — 문 밖 추격은 없다.
    ///     원거리 종은 사거리 안에서 멈춰 쏜다.
    /// </summary>
    private static void UpdateSupplyMonsterMovement(
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        DateTime now,
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

        // 추격 대상 (#229 저주기 갱신): 들고 있는 대상이 같은 구역에 있으면 그대로 쫓는다.
        // 재선정은 대상을 잃었거나 유지 창(1초)이 끝났을 때만 — 매 틱 최근접을 다시 고르면
        // 두 사람 사이에서 방향이 떨리고, 무리 전체가 같은 사람에게 순간 쏠린다.
        bool found = false;
        var target = default(SpotArenaPlayerSpatial);
        for (int index = 0; index < participants.Count && monster.ChaseTargetPlayerId != 0; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area || participant.PlayerId != monster.ChaseTargetPlayerId)
                continue;
            target = participant;
            found = true;
            break;
        }

        if (found && now >= monster.NextTargetScanAtUtc)
        {
            monster.NextTargetScanAtUtc = now.AddSeconds(SupplyTargetHoldSeconds);
            found = false;
        }

        if (!found)
        {
            // 재선정: 같은 구역 최근접.
            float nearestSquared = float.MaxValue;
            for (int index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.Area != monster.Area)
                    continue;
                float dx = participant.Position.X - monster.Position.X;
                float dy = participant.Position.Y - monster.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= nearestSquared)
                    continue;
                nearestSquared = distanceSquared;
                target = participant;
                found = true;
            }

            if (found)
                monster.NextTargetScanAtUtc = now.AddSeconds(SupplyTargetHoldSeconds);
        }

        if (!found)
        {
            // 구역에 아무도 없다 — 앵커 복귀, 도착하면 다시 잠든다.
            var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
            float homeDx = anchor.X - monster.Position.X;
            float homeDy = anchor.Y - monster.Position.Y;
            if (homeDx * homeDx + homeDy * homeDy <=
                CampReturnArriveDistance * CampReturnArriveDistance)
            {
                monster.Aggro = false;
                monster.ChaseTargetPlayerId = 0;
                return;
            }

            MoveTowardPlayer(monster, anchor, deltaSeconds);
            return;
        }

        monster.ChaseTargetPlayerId = target.PlayerId;
        if (monster.AttackRangeValue > ContactRange)
        {
            float holdDx = target.Position.X - monster.Position.X;
            float holdDy = target.Position.Y - monster.Position.Y;
            float holdRange = monster.AttackRangeValue * RangedHoldRangeRatio;
            if (holdDx * holdDx + holdDy * holdDy <= holdRange * holdRange)
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

        // 도주 목적지 약속 (#226): 봇별 최근 도주 목적지 — 재계획 폭주에도 방향을 유지한다.
        public Dictionary<long, (Vector3f Destination, DateTime CommittedAtUtc)> BotFleeCommitments { get; } =
            new();

        // 점유 구역 공급 (#229 4단계): 점유 중인 열린 구역만 항목을 갖는다 — 비점유·폐쇄 시 삭제.
        public Dictionary<AreaType, SupplyZoneState> SupplyZones { get; } = new();

        // 소환석 예산 원장 (#229 4단계): 구역·페이즈별 잔량. 보충 타이머(SupplyZones)와 달리
        // 구역을 비웠다 돌아와도 살아남는다 — 들락날락으로 예산을 리셋하면 보상이 무제한이 된다.
        public Dictionary<(AreaType Area, int PhaseIndex), int> SupplyStoneBudget { get; } = new();

        // 핵 보상 정산 (#229 4단계): 석을 준 (구역, 페이즈) 조합 — 같은 칸에서 두 번째 핵부터는 몸만.
        public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
        public int NextSupplyPackOrdinal { get; set; }
        // 전역 상한 기준 인원 (#226 E): 생존자 수가 아니라 매치 최대 참가 수로 8인/10인을 가른다.
        public int MaxParticipantCount { get; set; }
    }

    /// <summary>공급 구역 사이클: 무리 2회(첫 즉시 + 8초 증원) 뒤 고갈 → 30초 휴식 뒤 후보 복귀. 은퇴는 축소·폐쇄용.</summary>
    private sealed class SupplyZoneState
    {
        public DateTime? NextTopUpAtUtc { get; set; }
        public DateTime? WipeRestUntilUtc { get; set; }
        public bool HasSpawned { get; set; }
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
        public int HeartReward { get; init; }
        public int BootsReward { get; init; }
        public int KeyReward { get; init; }
        public int ContactDamageValue { get; init; }
        public long ChaseTargetPlayerId { get; set; }

        // 몬스터 4종: 종별 스탯은 스폰 시 박제된다. 레거시 스폰 경로는 기본값(해골 상당)을 쓴다.
        public SwarmMonsterKind Kind { get; init; } = SwarmMonsterKind.Skeleton;
        public int MaxHealthValue { get; init; } = MonsterMaxHealth;
        public float AttackRangeValue { get; init; } = ContactRange;
        public float AttackCooldownValue { get; init; } = ContactCooldownSeconds;

        // 캠프 모드: 소속 캠프와 제자리(앵커), 어그로 상태.
        public int CampIndex { get; set; } = -1;
        public DateTime NextTargetScanAtUtc { get; set; }
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
            // 잼 보상은 패킷 모델에 싣지 않는다 — SwarmArenaDamageResult로 서버 내부 전달.
            ChaseTargetPlayerId = ChaseTargetPlayerId,
            // 종을 따로 싣는다 (#229 4단계): 크기·몸체가 더 이상 피통에 묶이지 않는다.
            Kind = (int)Kind
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
    Bowler = 3,

    // 보스 (#223): 중간 지대 고정 캠프 1기, 리스폰 없음 — 맵의 유한 대형 콘텐츠.
    Golem = 4,
    BabyDragon = 5,
    TreeGiant = 6
}

public sealed class SwarmArenaTickResult
{
    public List<SpotArenaPlayerDamage> PlayerDamage { get; } = new();
    public List<MonsterRuntimeInfo> SpawnedMonsters { get; } = new();
    public List<SupplyPackSpawnInfo> SupplyPackSpawns { get; } = new();
}

/// <summary>공급 무리 스폰 계측 (#226 E) — GameServer가 매치 이벤트 로그로 옮겨 적는다.</summary>
public readonly record struct SupplyPackSpawnInfo(
    AreaType Area, int PackIndex, int MonsterCount, int StoneTotal);

public readonly record struct SwarmArenaDamageResult(
    bool Applied,
    bool Killed,
    int MonsterId,
    MonsterRuntimeInfo? MonsterState,
    int HeartReward = 0,
    int BootsReward = 0,
    int KeyReward = 0,
    SwarmMonsterKind Kind = SwarmMonsterKind.Skeleton)
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
