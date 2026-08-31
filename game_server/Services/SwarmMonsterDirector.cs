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
public sealed class SwarmMonsterDirector
{
    // #219 초반 템포 하향 (2026-08-09): 시작 스쿼드가 오브 1개뿐이라 20(2방)도 캠프 하나에
    // 14초가 걸렸다. 해골 = T1 한 방(12) — 초반 파밍이 사격 몇 번으로 끝나야 SB "코인 몹"
    // 감각이 산다.
    public const int MonsterMaxHealth = SwarmMonsterArchetypeCatalog.DefaultMonsterMaxHealth;

    // 실측(2026-08-05): 30 + 무적 0.6초 조합은 18초 생존으로 끝났다. 연속 접촉 기준
    // 최소 사망 시간이 충분히 길도록 24 × 0.8초를 유지한다.
    public const int ContactDamage = 24;

    // 시작방 몹은 약하게: 봇 포함 8인 전원이 초반 2팩을 버티고 조우 지점까지 살아나가야
    // 조우 구도가 성립한다. 위험 경사는 시작방(약) → 조우 지점·외곽(강)으로 유지.
    public const int StartRoomContactDamage = 12;

    // 몬스터별 쿨다운만 있으면 무리에 겹칠 때 마릿수만큼 중첩 피격되어 1~2초 만에 죽는다.
    // 뱀서 표준대로 참가자 측 피격 무적을 둔다: 한 입은 아프게, 무리는 초당 몇 입만.
    //
    // 0.8 → 0.4 (#229 4단계-보정, 진단서 4번): 이 창이 플레이어당 전역이라 몇 마리가 붙어도
    // 초당 1.25대가 상한이었다 — 밀도를 4배로 올려도 위협은 그대로였고, 25마리 한가운데가
    // 1마리와 같았다. 이게 뱀서 후반 위협의 정체다: TTK가 아니라 둘러싸임.
    //
    // 0.4 → 0.6 재조정 (사람 매치 2694 실측): 접촉 피해 상향과 겹쳐 과했다. 초당 1.1회 피격 ·
    // 피격당 오염 1~2로 오염이 초당 1.93씩 차, 4분이면 아무것도 안 해도 만충이었다.
    // 0.6이면 초당 상한 1.67회로 5분 매치 끝에 위험해지는 수준이 된다 — 수면으로 회복하면 산다.
    public const float ContactImmunitySeconds = 0.6f;

    // 접촉 반경 = 보이는 몸통 (#229, 클라 실측): 해골 몸통 스프라이트는 폭 0.62 · 반폭 0.31인데
    // 판정은 전 종 고정 0.45였다 — 스프라이트보다 45% 큰 원이라 옆을 스쳐도 맞았다.
    // 반폭에 맞춰 눕히고, 종별 크기는 GetContactRadius가 클라 ResolveKindScale과 같은 사다리로 따라간다.
    // 서버 위치는 클라 예측보다 늦으므로 회피자에게 후한 쪽이 맞다.
    public const float ContactRange = SwarmMonsterArchetypeCatalog.DefaultContactRadius;
    public const float ContactCooldownSeconds = SwarmMonsterArchetypeCatalog.DefaultContactCooldownSeconds;
    // 4.2 → 3.2(08-17 오후, "몹이 너무 빠르다") → 4.2 복구 (08-17 저녁 유저 결정: 몹이 달려들어
    // 부딪혀야 한다 — 숨쉬는 포위와 함께 내렸던 이속을 직진 추격 복귀와 함께 되돌린다).
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
    // 참가자 피해 절반 배율(SwarmMonsterDamageTakenMultiplier)을 지난 값이 실효다.
    //
    // 접촉 피해 상향 (#229 4단계-보정, 진단서 4번): 원안 1/1/2/2/3은 배율 통과 후 1/1/1/1/2라
    // 오염 상한 420 기준 상시 접촉 사망까지 336초가 걸렸다 — 매치 300초보다 길어 몹이 사람을
    // 수학적으로 못 죽였다. 봇 10인 매치에서 180초 동안 탈락 0건이 그 증거다.
    // 2/3/4/6/8로 올리면 실효 1/2/2/3/4가 되어 상시 접촉 사망이 140초 근처로 들어온다.
    // 밀도를 올려도 무적창(0.8초)이 플레이어당 전역이라 위협은 마릿수가 아니라 이 값이 정한다.
    //
    // 소환석 예산 3배 상향 (#229, 매치 9703595 실측): 원안 10/11/12/13/13은 150초에 인당
    // 15석, 5분 완주 기준 ~30석뿐이라 성장 3회(누적 21석)에서 멈췄다. 몹은 석 1077개어치를
    // 들고 죽었는데 예산이 그중 89%를 잘라내고 있었다. 성장 8회(누적 96석 · 곡선 5+2N)를
    // 5분 안에 닿을 목표로 잡고 30/33/36/39/39로 올린다 — 예산은 여전히 몹 보유량보다
    // 훨씬 낮아 "한 구역 무한 파밍" 차단이라는 원래 역할은 그대로다.
    //
    // 밀도 재하향 (#229, 사람 매치 2693 실측): 8→60은 과했다. 초당 2.4마리를 죽이는데도 수가
    // 줄지 않아 "몹이 안 죽는다"로 읽혔고, 초당 1.4회 피격으로 회피가 불가능했다.
    // 8/12/16/22/28로 내린다 — 0:00 대비 4:10이 3.5배로 여전히 램프이되 화면이 감당된다.
    // 소환석 예산은 30→45 계열로 올린다: 같은 구간에 성장 4회(비용 11·13·15·17)뿐이라
    // 후반에 19석 오퍼가 떠도 못 샀다.
    //
    // 밀도 램프와 HP 하향 (#229 4단계-보정): 원안(목표 9→18 · HP 12→30)은 두 가지가 틀렸다.
    // 첫째, 목표가 전역 상한 48에 가려 한 번도 도달하지 못해 5분 내내 구역당 4~6마리였다 —
    // 동시 적 수 배수 ×1.0, 장르 기준(×10~20)의 바깥이다. 목표를 8→60으로 올린다.
    // 둘째, HP 배수(×2.5)가 오브 발당 피해 배수(12→30 = ×2.5)와 정확히 같아 타격 수가
    // 개선되지 않았다. T1은 오히려 1방에서 3방으로 역주행했다.
    // HP를 16/17/19/21/22로 눕혀 T1은 전 구간 2방으로 고정하고, T2(발당 21)를 얻는 순간이
    // 곧 "한 방이 되는 순간"이 되게 한다. 핵 HP 곡선은 유지한다 — 핵은 "아직 한 방이 아닌 것"의
    // 눈금자다.
    // 목표는 구역이 아니라 사람 단위다 (2026-08-16 유저 판정: 몹 곡선이 거꾸로 간다).
    // 구역 절대값이던 시절, 목표는 그 방에 몇 명이 서 있든 같았다 — 초반 교실에 혼자면 8마리를
    // 독차지하지만 후반 운동장에 8명이 모이면 28마리를 나눠 3.5마리가 됐다. 폐쇄로 방이 줄고
    // 사람이 겹칠수록 1인당 밀도가 떨어지는 구조라, 매치 2749에서 처치가 1121→344로 반토막
    // 나고 소환석도 585→122로 말랐다. 뱀서라이크는 시간이 갈수록 감당이 안 되는 게 전부인데
    // 정확히 반대로 갔다.
    // 인당 목표로 바꾸면 혼자 있을 때의 체감은 그대로고(1명 × 목표 = 예전 구역 목표),
    // 모일수록 총량이 따라 붙어 밀도가 유지된다.
    private static readonly (double UntilSeconds, int PerPlayerTarget, int NormalHp, int ContactDamage,
        int CoreHp, int StoneBudget)[] SupplyPhases =
    [
        // 잘 죽되 맞으면 치명적 (2026-08-16 유저 결정). 한 마리를 단단하게 만드는 방향은
        // 되돌린다 — "몹이 약하다"는 HP가 아니라 위협을 가리킨 말이었고, HP를 올린 건
        // 오독이었다. 뱀서라이크의 몹은 한두 방에 녹지만 닿으면 크게 아프다.
        // HP는 16/17/19/21/22로 되돌려 T1을 전 구간 2방에 고정하고(T2를 얻는 순간이 곧
        // 한 방이 되는 순간), 위협은 전부 접촉 피해가 진다.
        // 접촉 피해 10/14/20/28/40 — 오브 HP는 T1 24 · T2 56 · T3 120이므로 최종 페이즈에
        // T1은 한 방, T3도 세 방에 깨진다. 빈손 환산(피해 × 8.75 오염, 상한 420)으로는
        // 최종 페이즈 두 방이 죽음이다. 봇 매치 9871477 실측에서 5분에 탈락이 1명뿐이었다.
        (100d, 8, 16, 10, 48, 90), // 0:00~1:40 폐쇄 전
        (150d, 12, 17, 14, 60, 100), // 1:40~2:30 1차
        (200d, 16, 19, 20, 72, 110), // 2:30~3:20 2차
        (250d, 22, 21, 28, 96, 120), // 3:20~4:10 3차
        (double.MaxValue, 28, 22, 40, 120, 120) // 4:10~5:00 최종 수렴
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

    // 보충률은 처치율 위에 둔다 (#229 4단계-보정). 원안 2마리/1.5초 = 1.33마리/초는
    // 실측 처치율 3.2~5.2마리/초의 3분의 1이라 방이 항상 비어 있었다 — 플레이어가 보는 건
    // 벽이 아니라 간헐적 소규모 청소였다.
    //
    // 웨이브로 끊는다 (#229 실플레이 판정): 6마리/0.6초는 총량은 맞지만 끊임없이 졸졸
    // 흘러 "밀려온다"가 아니라 "계속 있다"로 읽혔다. 한 번에 크게 붓고 쉬어야 밀려오는
    // 파도가 되고, 그 사이가 곧 정리하고 숨 돌리는 창이다.
    // 20마리/2초 = 10마리/초 — 처치율 대비 총량은 그대로 두고 리듬만 바꾼다.
    // 실제 투입량은 구역 목표에 다시 잘리므로(want = min(count, 목표 - 생존)) 초반에는
    // 목표치가, 후반에는 이 값이 한 웨이브 크기를 정한다.
    // 웨이브 간격 (2026-08-16 유저 요구: 리젠이 수치로 정해져야 한다).
    // 2초마다 부족분을 채우는 "인구 유지" 모델은 웨이브가 아니라 끊임없는 졸졸 흐름이었다.
    // 12초에 한 번, 구역 목표까지 한 번에 붓는다 — 그 사이가 정리하고 숨 돌리는 창이다.
    // 한 웨이브 크기 = 구역 목표 - 생존 수 (상한 SupplyTopUpCount).
    private const double SupplyTopUpIntervalSeconds = 12d;
    private const int SupplyTopUpCount = 30;
    // 일반 몹 하트 드롭 확률 (2026-08-16). 처치율 2~3/초 기준 12~20초에 하나꼴 —
    // 흐름이 끊기지 않을 만큼만이고, 몰아 잡을수록 회복도 몰린다.
    private const double SupplyHeartDropChance = 0.03d;

    // 구역 전멸 뒤 휴지: 짧은 수면 창이 성장의 보상이다 (#229 완료 조건 2).
    // 웨이브 간격(12초)이 이미 창을 만들므로 전멸 휴지는 짧게만 둔다 — 다 지운 뒤에도
    // 12초를 더 기다리면 방이 너무 오래 빈다.
    private const double SupplyWipeRestSeconds = 2d;
    // 최종 수렴 페이즈에는 휴지가 없다 (#232 1단계): 몬스터는 교차사격의 기준점이라 마지막
    // 구역에서 몹이 비면 PvP도 같이 멎는다. 완료 조건 "최종 30초 기준점 부재 3초 이하".
    private const double SupplyWipeRestSecondsFinalPhase = 0d;
    // 플레이어 2.5m 안의 앵커에는 즉시 생성하지 않는다 — 전 앵커가 막히면 1초 뒤 재검사.
    private const float SupplySafeSpawnDistance = 2.5f;
    // 화면 밖 등장 (#229): 이 거리 밖 앵커를 우선 고른다. 전부 가까우면 안전 이격만 지킨다.
    private const float SupplyOffscreenDistance = 7f;

    // 안쪽(운동장) 편향 (#229 4단계-보정): 잔상이 중앙에서 번져 나오는 것처럼 보이게 한다.
    private static readonly AreaType SwarmInwardOriginArea = Config.SWARM_MATCH_GROUND_AREA;
    private const float SupplyInwardBias = 2.2f;
    private const double SupplyBlockedRetrySeconds = 1d;
    private const int SupplyCoreStoneReward = 3;

    // 핵(큰 몹)이 처음 서는 페이즈 (#229): 0·1페이즈(0:00~2:30)는 작은 몹만 나온다.
    private const int SupplyCoreFirstPhaseIndex = 2;

    // 추격 대상 유지 창 (#229): 이 시간이 지나야 최근접을 다시 고른다.
    private const double SupplyTargetHoldSeconds = 1d;

    // 유휴 순찰 (#269-B): 주인 잃은 몹의 앵커 주변 배회 — 반경·각속도(호속 ≈ 1.1/s).
    private const float SupplyIdlePatrolRadius = 2.2f;
    private const double SupplyIdlePatrolAngularSpeed = 0.5d;

    // 전역 활성 잔상 상한 (#229 4단계-보정): 고정 48은 개전 3초에 물려 밀도 램프를 통째로
    // 가렸다. 점유 구역 수 × 페이즈 목표로 풀되 서버 안전 천장을 둔다.
    // 클라 부하는 스냅샷을 구역별로만 보내는 것으로 분리했다(BroadcastMonsterMinimapSnapshot) —
    // 시뮬은 전역, 동기화는 내 구역뿐이라 한 사람이 받는 양은 구역 목표를 넘지 않는다.
    private const int SupplyGlobalAliveHardCap = 420;

    // 한 구역이 인당 목표의 몇 명분까지 부풀 수 있는지 (2026-08-16). 인당 비례를 그대로 두면
    // 최종 페이즈에 8명이 운동장에 모일 때 224마리가 한 화면에 서고, 클라 렌더 부하가 검증된
    // 적이 없다. 4명분에서 끊어 두면 그래도 1인당 14마리로 지금(3.5마리)의 4배다.
    private const int SupplyZoneCrowdCap = 4;

    // 세 번째 사람부터는 인당 증가량의 절반만 더한다 (#232 1단계 확정 규칙: "사람 수에 비례해
    // 몬스터를 선형으로 늘리지 않는다. 3명 이상이 모였을 때 추가 공급은 기본 증가량의 50%부터").
    // 몬스터가 교차사격 기준점이 된 뒤로는 사람이 몰린 방의 몹 수가 곧 예고 밀도라, 선형이면
    // 4인 방에서 화면이 예고로 덮인다. 1인 T · 2인 2T · 3인 2.5T · 4인 3T.
    private const double SupplyCrowdExtraRatio = 0.5d;
    private const int SupplyCrowdLinearPlayers = 2;

    /// <summary>
    ///     구역 목표 = 인당 목표 × 그 구역에 선 사람 수. 최소 1명분은 항상 준다 —
    ///     인트로 산개는 아무도 없는 방을 대상으로 돌고, 사람이 막 빠져나간 방도 다음 틱까지는
    ///     채워져 있어야 한다. 셋째 사람부터는 절반 비율로 는다(SupplyCrowdExtraRatio).
    /// </summary>
    private static int GetSupplyZoneTarget(int perPlayerTarget, int playersInZone)
    {
        int players = Math.Clamp(playersInZone, 1, SupplyZoneCrowdCap);
        int linearPlayers = Math.Min(players, SupplyCrowdLinearPlayers);
        int crowdPlayers = players - linearPlayers;
        return perPlayerTarget * linearPlayers +
               (int)Math.Round(perPlayerTarget * SupplyCrowdExtraRatio * crowdPlayers);
    }
    // 1초 예고는 밀도 램프에서 실질 병목이 된다 — 초당 10마리를 채우는데 전부 1초를 서 있으면
    // 화면에 "아직 안 깨어난 몹"만 쌓인다 (#229 4단계-보정).
    private const float SupplyTelegraphSeconds = 0.4f;
    private const float SupplyScatterRadius = 1.6f;
    // 원거리 종(다트·볼러)은 사거리의 이 비율에서 멈춰 쏜다 — 근접 종만 몸으로 파고든다.
    private const float RangedHoldRangeRatio = 0.8f;

    public const float CampAggroRadius = 2.5f;
    // 리쉬는 봇 사격 대역(5~7)보다 짧게 — 추격이 빨리 끊겨야 카이팅 사이클이 성립한다.
    public const float CampLeashRadius = 5.5f;
    private const int CampsPerArea = 3;
    // 스폰 셀(방 중앙)과 캠프 사이 안전 이격 — 스폰 포켓은 SB처럼 비워 둔다.
    private const float CampAnchorRadius = 6f;
    private const float CampScatterRadius = 1.2f;
    private const double CampRespawnSeconds = 45d;
    private const float CampReturnArriveDistance = 0.4f;

    // ===== 침투 (#229) =====
    // 웨이브는 구역 안에서 솟지 않는다. 운동장(최종 폐쇄 구역) 중심에서 태어나 배정 구역까지
    // 걸어 들어간다 — 발원지가 폐쇄의 종착점이라 "중앙에서 번져 나와 바깥을 밀고 들어간다"는
    // 흐름이 폐쇄 방향과 정면으로 마주 본다.
    // 이 구조가 필요한 실측 근거: 앵커에 잠들어 붙박인 모델에서는 처치율이 오브 수와 무관하게
    // ZoneTarget만 따라갔다 (봇 매치 9840982, 199초 931킬 — 페이즈 0/1/2에서 1.85·2.6·3.2/s로
    // ZoneTarget 8/12/16에 정비례, 성장 구성이 다른 두 봇의 처치율이 동일). 마주치는 수가
    // 앵커 하나 분량에 고정되니 화력을 올려도 살 게 없었다.
    private const float InfiltrationOriginRadius = 3.5f;
    // 원형 산개 (#232, 2026-08-17): 발원 각도 = 목적지 방향 ± 50°(황금각 수열 지터), 첫 웨이포인트는
    // 방사 2.0m 바깥. 같은 경로 위에서는 레인 ±0.4·속도 0.9~1.1×로 한 줄이 아니라 띠로 걷는다.
    private const double InfiltrationGoldenAngle = 0.6180339887498949d;
    private const float InfiltrationOriginJitterRadians = 1.3963f;
    private const float InfiltrationBurstDistance = 2.0f;
    private const float MarchLaneOffsetMax = 0.4f;
    private const float MarchSpeedJitter = 0.1f;
    private const double InfiltrationDepartureJitterSeconds = 0.4d;
    private const float MarchWaypointArriveDistance = 0.6f;
    private const float RouteSampleStep = 0.35f;
    // 문어귀 통과 허용 길이 (2026-08-16 실측 보정). 4샘플(1.4)로는 좁았다 — 봇 매치
    // 9855085에서 행정실만 몹 55마리 배정에 처치 0으로, 그 방 경로가 통째로 폐기되고 있었다.
    // BotPathfinder의 문 통과는 근측 보행 셀 → 원측 보행 셀 한 번의 도약이라 문틀 + 양쪽
    // 여유까지 걸치고, 아이소메트릭 대각이면 더 길어진다. 10샘플(3.5) — 벽 한 장은 이보다
    // 훨씬 길므로 관통 방지는 유지된다.
    private const int DoorwayBlockedSampleTolerance = 10;
    // 행군 제한: 경로가 길수록 넉넉히 주되, 막히면 낭비 없이 걷어낸다.
    // 웨이포인트 수 × 고정 계수는 폐기 (2026-08-17): 웨이포인트 간격이 균일하지 않아 긴 경로가
    // 계수 추정을 넘고, 이속을 4.2 → 3.2로 내리자 정상 행군까지 예산 초과로 12초마다 걷히고
    // 다시 스폰되는 순환이 생겼다(실측). 실제 경로 길이 / 이속 × 여유로 계산한다.
    private const double MarchBudgetSlackMultiplier = 1.8d;
    private const double MarchBudgetMinimumSeconds = 8d;

    /// <summary>행군 예산 = 경로 실거리 기준 소요 시간 × 여유. 최소 8초.</summary>
    private static double ComputeMarchBudgetSeconds(Vector3f start, IReadOnlyList<Vector3f> route)
    {
        double length = 0d;
        var previous = start;
        for (int index = 0; index < route.Count; index++)
        {
            float dx = route[index].X - previous.X;
            float dy = route[index].Y - previous.Y;
            length += MathF.Sqrt(dx * dx + dy * dy);
            previous = route[index];
        }

        return Math.Max(MarchBudgetMinimumSeconds, length / MonsterMoveSpeed * MarchBudgetSlackMultiplier);
    }

    // #219 SB 몬스터 4종 (원작 스펙 ÷25 환산, 잼 보류 — 보상은 소환석만).
    // 해골: 무해한 코인 파밍 무리. 다트: 접촉 단발(부츠 드롭). 탈주: 접촉 강펀치 브루저.
    // 볼러: 접촉 범위 강타. 일반 몹 원거리 타격은 퇴역 (2026-08-24 유저 지시: 몹 외형이 전부
    // 근거리라 서서 때리는 원거리가 이상해 보임) — 원거리는 고정 포대인 보스만 남는다.
    public const float BowlerSplashRadius = 1.5f;

    // 파도 문양 몹: 접촉 강타가 주변까지 튄다 — 보드의 파도 오브(물폭탄 스플래시)와 같은 문법.
    // 원거리 유지 사거리(3.2)는 2026-08-24 원거리 퇴역과 함께 제거 — 문양의 정체는 스플래시가 진다.
    public const float WavePatternSplashRadius = 2.2f;
    private const float WavePatternAttackCooldownSeconds = 2.2f;

    /// <summary>파도 문양(Encircle 패턴) 몹인가 — 표기 문양과 공격 방식이 같은 근거를 쓴다.</summary>
    public static bool IsWavePatternMonster(SwarmPattern pattern) =>
        SwarmMonsterArchetypeCatalog.IsWavePattern(pattern);

    /// <summary>
    ///     종별 스탯 원본은 swarm_monster.csv (#292 CSV 이전). attack_range 0 = 접촉 몹(ContactRange 폴백).
    ///     피통은 클라 종 식별자이기도 하다 — SwarmAfterimageMonsterDisplay 스위치와 동기 필수.
    ///     보스 사거리(4.1)는 Config.SWARM_BOSS_ATTACK_RANGE(클라 범위 링)와 동기 필수.
    /// </summary>
    public static (int MaxHp, int OrbDamage, float AttackRange, float AttackCooldownSeconds, int StoneReward,
        int HeartReward, int BootsReward, int KeyReward)
        GetKindStats(SwarmMonsterKind kind) => SwarmMonsterArchetypeCatalog.GetStats(kind);

    /// <summary>보스 판별 (#223): 고정 포대·리스폰 없음·타원 판정 공유의 스위치.</summary>
    /// <summary>
    ///     종별 접촉 반경 (#229). 클라 ResolveKindScale과 같은 사다리를 쓴다 — 보이는 몸통이 판정이다.
    ///     원거리 몹의 사거리(AttackRangeValue)는 별개다. 이건 부딪힘 반경만 정한다.
    /// </summary>
    public static float GetContactRadius(SwarmMonsterKind kind) =>
        SwarmMonsterArchetypeCatalog.GetContactRadius(kind);

    public static bool IsBossKind(SwarmMonsterKind kind) =>
        SwarmMonsterArchetypeCatalog.IsBoss(kind);

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

    /// <summary>파도 문양 몹인가 — 접촉 강타가 스플래시로 튀므로 공격 연출을 따로 보내야 읽힌다.</summary>
    public bool IsWavePatternMonster(long matchingId, int monsterId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            return state.Monsters.TryGetValue(monsterId, out var monster) &&
                   !IsBossKind(monster.Kind) && IsWavePatternMonster(monster.Pattern);
        }
    }

    /// <summary>
    ///     보스 상주 구역 (#223): 중간 지대 캠프 0번이 보스 단독 캠프가 된다.
    ///     School 맵의 실존 중간 지대는 3곳뿐 — 북 밴드(정크장)·남 밴드(회랑)·운동장.
    ///     트리 자이언트(열쇠)는 운동장 — 광산(150초 개장)과 같은 무대의 선주민 수호자.
    /// </summary>
    private static bool TryGetBossKind(AreaType area, out SwarmMonsterKind kind)
    {
        return SwarmMonsterArchetypeCatalog.TryGetResidentBoss(area, out kind);
    }

    private const int FirstMonsterId = 7_000_000;
    private const long FirstCombatTargetId = -4_000_000_000_000_000_000L;
    private const float RingRadius = 9f;
    private const float RushDistance = 11f;
    private const float RushLateralSpread = 1.6f;
    private const float EncircleRadius = 4.5f;
    // 포위 오프셋 (#232 2026-08-17 유저 결정: 몹이 뭉쳐 따라오지 말고 축이 될 만큼 산재).
    // 접근 목표 = 플레이어 + 개체 고유 각도의 오프셋. 반경은 추격하는 동안 줄어들어 결국 접촉한다 —
    // 사방에서 조여드는 흩어진 고리가 되고, 교차사격 기준점이 여러 방위에 선다.
    // 바닥면은 아이소라 Y 오프셋은 절반(dy×2 정규화의 역).
    // 포위 스위치 (2026-08-17 저녁 유저 결정: 몹이 플레이어에게 달려들어 부딪혀야 한다 — 숨쉬는 포위는
    // 사람 매치 로그에서 몹 접촉 피해 0건). 끄면 직진 추격. 코드는 남긴다 — 켜면 산재 고리로 돌아간다.
    private const bool SurroundEnabled = false;
    private const float SurroundStartRadius = 3.5f;
    private const float SurroundShrinkPerSecond = 0.35f;
    // 반경 0 이후 이만큼 더 감쇠하는 동안 플레이어를 직격한다(접촉 창 ≈ 1.7초), 그 뒤 고리로 복귀.
    private const float SurroundLungeDepth = 0.6f;
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
        MatchSpawnData.GetPhaseRoomCandidates().ToHashSet();

    // M4 격화: 폐쇄 웨이브와 동기화된 시간 단계. 접촉 데미지는 올리지 않는다 —
    // TTK가 아니라 밀도·페이스·이속만 조인다 (결정 브리프 2026-08-06).
    private const double EscalationStage1AtSeconds = 120d;
    private const double EscalationStage2AtSeconds = 230d;
    private const float EscalationStage2MoveSpeedMultiplier = 1.1f;

    /// <summary>
    ///     몹 스폰 스위치 (2026-08-18 촬영용): DEV_NO_MONSTERS=1이면 어떤 경로로도 잔상을 세우지 않는다.
    ///     compose 환경변수라 켜고 끄기는 컨테이너 재생성. 테스트는 기본값(켬)을 본다.
    /// </summary>
    public static bool MonsterSpawnEnabled { get; set; } =
        Environment.GetEnvironmentVariable("DEV_NO_MONSTERS") != "1";

    /// <summary>
    ///     #272 경계 토출 스폰 리졸버 — GameServer가 주입한다. 자기장 경계가 구역을 관통 중이면
    ///     (스폰 셀 = 경계 밖 빨간 띠, 앵커 셀 = 경계 안 띠)를 주고, 구역이 온전히 안전하면 null.
    /// </summary>
    public static Func<long, AreaType, (Cell Spawn, Cell Anchor)?>? FieldSpawnCellResolver { get; set; }

    /// <summary>폐쇄된 구역은 신규 스폰을 멈춘다 — 잔존 몹은 이주로 처리된다.</summary>
    public Func<long, AreaType, bool>? IsAreaClosedResolver { get; set; }

    /// <summary>매치가 시작됐는가. 없으면 시작된 것으로 본다 — 봇 전용 매치는 게이트가 없다.</summary>
    public Func<long, bool>? IsGameplayActiveResolver { get; set; }

    /// <summary>
    ///     오브가 하나도 없는가 (2026-08-16 유저 명세: 무오브는 잔상의 우선 표적).
    ///     무오브는 자동 공격도 절단도 못 하므로, 잔상까지 남을 쫓으면 구석에서 재건하는
    ///     동안 아무 압력도 안 받는다 — 그러면 무오브가 안전지대가 된다.
    ///     주인 배정·재배정에서 무오브를 먼저 채운다.
    /// </summary>
    public Func<long, long, bool>? IsPlayerOrblessResolver { get; set; }

    private bool IsOrbless(long matchingId, long playerId) =>
        IsPlayerOrblessResolver?.Invoke(matchingId, playerId) == true;

    private static int GetEscalationStage(double elapsedSeconds) =>
        elapsedSeconds >= EscalationStage2AtSeconds ? 2 :
        elapsedSeconds >= EscalationStage1AtSeconds ? 1 : 0;

    private readonly ConcurrentDictionary<long, MatchState> _matches = new();
    private readonly Func<DateTime> _utcNow;

    public SwarmMonsterDirector(Func<DateTime>? utcNow = null)
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
        // #272 School2: 복도(연결로 1~8)는 무스폰 통로 — 병합된 가운데(S2Corridor9)는
        // 운동장 프로파일(아래 Config 그라운드 분기)을 탄다.
        if (area >= AreaType.S2Corridor1 && area <= AreaType.S2Corridor8)
            return CampModeEnabled ? (12, 9d) : (0, 0d);
        if (area == Config.SWARM_MATCH_GROUND_AREA)
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

            // 인트로 예열 구간인가 — 침투가 문턱에서 멈춘다 (2026-08-16 유저 판정).
            bool preMatch = IsGameplayActiveResolver?.Invoke(matchingId) == false;

            // 격화 2단계: 이속만 소폭 상승 — 접촉 데미지는 불변 (M4).
            double moveDeltaSeconds = GetEscalationStage((now - state.StartsAtUtc).TotalSeconds) >= 2
                ? deltaSeconds * EscalationStage2MoveSpeedMultiplier
                : deltaSeconds;

            // 몹 스폰 끄기 (DEV_NO_MONSTERS=1, 2026-08-18 촬영용): 공급·캠프·레거시 어느 경로도 세우지 않는다.
            // 봇·전투·폐쇄는 그대로 돈다 — 잔상 없는 판이 필요할 때(영상·PvP만 검증) 쓴다.
            if (!MonsterSpawnEnabled)
            {
                // 아무것도 안 함
            }
            else if (RegionSupplyModeEnabled)
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

            int probeAlive = 0, probeAggro = 0, probeChasing = 0, probeSameArea = 0, probeCooldown = 0;
            int probeInRange = 0, probeImmuneBlocked = 0, probeWithinOne = 0;
            float probeDistanceSum = 0f;
            float probeNearest = 9999f;
            foreach (var monster in state.Monsters.Values)
            {
                if (!monster.Alive || now < monster.ActivatesAtUtc)
                    continue;

                if (RegionSupplyModeEnabled)
                {
                    UpdateSupplyMonsterMovement(
                        state, monster, state.LastParticipants, now, moveDeltaSeconds, preMatch,
                        playerId => IsOrbless(state.MatchingId, playerId));
                    // 벽 탈출 안전망 (2026-08-16 유저 제보: 운동장에 벽에 낀 몹이 많다).
                    // 스폰·행군·추격 어느 경로로 들어갔든, 비보행 칸에 선 개체는 매 틱
                    // 보행 가능한 자리로 당긴다 — 원인을 하나 놓쳐도 화면에는 남지 않는다.
                    RescueMonsterFromBlockedCell(monster);
                    TrackStuckMonster(monster, now, result);
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

                probeAlive++;
                if (monster.Aggro) probeAggro++;
                if (monster.ChaseTargetPlayerId != 0) probeChasing++;
                // 추격 대상 기준으로 잰다 — 같은 구역 아무나 기준으로 재면 엉뚱한 사람까지의
                // 거리가 섞여 "안 붙는다"가 잘못 읽힌다 (2026-08-16 계측 수리).
                foreach (var probeParticipant in state.LastParticipants)
                {
                    if (probeParticipant.Area != monster.Area) continue;
                    if (monster.ChaseTargetPlayerId != 0 &&
                        probeParticipant.PlayerId != monster.ChaseTargetPlayerId) continue;

                    float pdx = probeParticipant.Position.X - monster.Position.X;
                    float pdy = (probeParticipant.Position.Y - monster.Position.Y) * 2f;
                    float pd = MathF.Sqrt(pdx * pdx + pdy * pdy);
                    if (pd < probeNearest) probeNearest = pd;
                    probeSameArea++;
                    probeDistanceSum += pd;
                    if (pd <= 1f) probeWithinOne++;
                    if (pd <= GetContactRadius(monster.Kind))
                    {
                        probeInRange++;
                        if (state.ContactImmuneUntilUtc.TryGetValue(
                                probeParticipant.PlayerId, out var probeImmune) && now < probeImmune)
                            probeImmuneBlocked++;
                    }

                    break;
                }

                if (now < monster.NextContactAtUtc)
                {
                    probeCooldown++;
                    continue;
                }

                // 잠든 원거리 몹은 저격하지 않는다 — 부딪힘(접촉 반경)만 개전이 된다.
                float attackRange = monster.Aggro && monster.AttackRangeValue > ContactRange
                    ? monster.AttackRangeValue
                    : GetContactRadius(monster.Kind);
                // 접촉 판정도 타원(dy×2) (#229): 보스만 쓰던 아이소 보정을 전 종에 적용한다.
                // 오브-플레이어 판정(IsInsideOrbHitEllipse)이 이미 쓰는 문법과 같다 —
                // 화면 세로가 절반으로 압축돼 있어 정원 판정은 세로로 스프라이트 밖까지 맞는다.
                const float verticalScale = 2f;
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
                    // 파도 문양 몹도 같은 경로를 쓰되 반경이 더 넓다 — 물폭탄이 플레이어 발밑에서
                    // 터지는 그림이라, 붙어 있는 사람이 같이 맞는 것이 규칙이다.
                    bool wavePattern = IsWavePatternMonster(monster.Pattern) && !IsBossKind(monster.Kind);
                    if (wavePattern ||
                        monster.Kind is SwarmMonsterKind.Bowler or SwarmMonsterKind.Golem)
                    {
                        float splashRadius = wavePattern ? WavePatternSplashRadius : BowlerSplashRadius;
                        foreach (var splashed in state.LastParticipants)
                        {
                            if (splashed.PlayerId == participant.PlayerId ||
                                splashed.Area != monster.Area)
                                continue;
                            float sx = splashed.Position.X - participant.Position.X;
                            float sy = splashed.Position.Y - participant.Position.Y;
                            if (sx * sx + sy * sy > splashRadius * splashRadius)
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

            if (now >= state.ContactProbeAtUtc)
            {
                state.ContactProbeAtUtc = now.AddSeconds(10);
                result.StuckReports.Add(
                    $"contact_detail alive={probeAlive} aggro={probeAggro} chasing={probeChasing} " +
                    $"sameAreaAsSomeone={probeSameArea} onCooldown={probeCooldown} " +
                    $"inRange={probeInRange} within1={probeWithinOne} immuneBlocked={probeImmuneBlocked} " +
                    $"avgDist={(probeSameArea > 0 ? probeDistanceSum / probeSameArea : -1f):F2} " +
                    $"nearest={(probeNearest > 9000f ? -1f : probeNearest):F2} " +
                    $"damage={result.PlayerDamage.Count}");
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

    /// <summary>
    ///     착탄 예약 (#229 과잉 사격 방지). 발사 시점에 피해를 미리 물려 두고 정산 때 푼다.
    ///     예약분만으로 이미 죽는 몹은 GetCombatTargets가 후보에서 빼므로, 다음 오브는 아직
    ///     살아남을 몹을 고른다. 화력이 곧 처치 수가 된다.
    /// </summary>
    public void ReserveMonsterDamage(long matchingId, long combatTargetId, int damage)
    {
        if (damage <= 0 || !_matches.TryGetValue(matchingId, out var state))
            return;

        lock (state.SyncRoot)
        {
            var monster = FindAliveByCombatTarget(state, combatTargetId);
            if (monster != null)
                monster.PendingDamage += damage;
        }
    }

    private static MonsterRuntime? FindAliveByCombatTarget(MatchState state, long combatTargetId) =>
        state.Monsters.Values.FirstOrDefault(candidate =>
            candidate.CombatTargetId == combatTargetId && candidate.Alive);

    /// <summary>
    ///     기준점 계측 (#232 1단계): 오브가 이 몹을 향해 공격 사건을 만든 순간 센다 — 착탄이
    ///     아니라 발사 기준이다. 교차사격 모양은 발사 순간 잠긴 기준점에서 생기므로, 몹이 몇 번의
    ///     모양을 만들고 죽는지는 이 수로 읽는다.
    /// </summary>
    public void RecordMonsterAttackEvent(long matchingId, long combatTargetId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return;
        lock (state.SyncRoot)
        {
            var monster = state.Monsters.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId);
            if (monster is { Alive: true })
                monster.AttackEventCount++;
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
                candidate.CombatTargetId == combatTargetId);
            // 예약은 생사와 무관하게 푼다 — 남겨 두면 살아 있는 몹이 영영 표적에서 빠진다.
            if (monster != null)
                monster.PendingDamage = Math.Max(0, monster.PendingDamage - damage);
            if (monster is not { Alive: true })
                return SwarmArenaDamageResult.None;

            if (RegionSupplyModeEnabled)
            {
                // 반격 개전: 맞은 몹과 같은 구역 무리 전체가 함께 깨어난다 — 무리는 한 덩어리다.
                // 다만 주인이 있는 개체는 표적을 넘기지 않는다 (2026-08-16): 오브는 사거리 안
                // 잔상을 쉬지 않고 쏘므로, 피격으로 표적이 넘어가면 남의 담당 몹이 통째로
                // 사격자에게 쏠려 "각자 할당"이 첫 발에 무너진다. 깨우기만 하고 표적은 둔다.
                monster.Aggro = true;
                if (monster.OwnerPlayerId == 0)
                    monster.ChaseTargetPlayerId = attackerPlayerId;
                foreach (var mate in state.Monsters.Values)
                {
                    if (!mate.Alive || mate.Aggro || mate.Area != monster.Area)
                        continue;
                    mate.Aggro = true;
                    if (mate.OwnerPlayerId == 0)
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
            var monsterInfo = monster.ToMonsterRuntimeInfo();
            if (killed)
            {
                DateTime diedAtUtc = _utcNow();
                monster.Alive = false;
                monster.DiedAtUtc = diedAtUtc;
                if (attackerPlayerId == state.HumanPlayerId)
                    state.Kills++;
                // 보상은 처치 시점에 구역·페이즈 예산에서 떼어 정한다 — 스폰 때 붙인 표기값이
                // 아니라 이 값이 실제 드롭이다.
                monsterInfo = monster.ToMonsterRuntimeInfo();
                monsterInfo.SummonStoneReward = ConsumeSupplyStoneBudget(state, monster, diedAtUtc);
            }

            return new SwarmArenaDamageResult(
                true, killed, monster.MonsterId, monsterInfo,
                monster.HeartReward, monster.BootsReward, monster.KeyReward, monster.Kind,
                AliveSeconds: killed ? (monster.DiedAtUtc - monster.SpawnedAtUtc).TotalSeconds : 0d,
                AttackEventCount: monster.AttackEventCount);
        }
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
                            MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, commitment.Destination),
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
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination),
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
                Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, bot.Area)),
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
                .Where(monster => monster.Alive && now >= monster.ActivatesAtUtc &&
                                  monster.Health > monster.PendingDamage)
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

    /// <summary>
    ///     침수 (#268, 2026-08-25): 소용돌이 피격 몹의 이동 감속. 변위(당김·밀침·축출) 실험은
    ///     전부 기각 — 체감이 없거나 과했다. 감속은 행군·추격 이동 양쪽에 적용된다.
    /// </summary>
    public bool TrySlowMonster(long matchingId, long combatTargetId, float slowSeconds, DateTime nowUtc)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return false;
        lock (state.SyncRoot)
        {
            var monster = state.Monsters.Values.FirstOrDefault(candidate =>
                candidate.CombatTargetId == combatTargetId && candidate.Alive);
            if (monster == null)
                return false;

            monster.WaveSlowUntilUtc = nowUtc.AddSeconds(Math.Max(0f, slowSeconds));
            return true;
        }
    }

    public SwarmMonsterSummary GetSummary(long matchingId)
    {
        if (!_matches.TryGetValue(matchingId, out var state))
            return SwarmMonsterSummary.Empty;
        lock (state.SyncRoot)
        {
            return new SwarmMonsterSummary(
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
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        Vector3f campAnchor;
        var customAnchorCell = GameMonsterCampData.GetAnchor(area, campIndex);
        if (customAnchorCell != null)
        {
            // 커스텀 앵커 (#219): monster_camp_anchor.csv가 지정한 셀. 지터 없이 고정 —
            // 저작한 위치가 곧 실배치다. walkable 클램프만 안전망으로 유지한다.
            campAnchor = ClampToAreaWalkable(
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, customAnchorCell), center, area);
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

        // #272 경계 토출 스폰 (2026-08-26 유저 결정, 같은 날 2차 "안전 구역 예외 제거"): 캠프는
        // 항상 바깥(자기장이 올 방향)에서 태어나 안쪽 앵커로 걸어 들어온다 — 경계 관통 중이면
        // 빨간 띠, 아직 안전한 구역이면 그 구역의 가장 바깥 띠. 저작 앵커는 자기장 모드 밖(리졸버
        // 미주입·null)에서만 쓴다. 오염이 잔상을 토해내는 그림 — 사냥터가 바깥 쪽이라 위험·보상이 겹친다.
        Vector3f? fieldHomeAnchor = null;
        var fieldSpawn = FieldSpawnCellResolver?.Invoke(state.MatchingId, area);
        if (fieldSpawn != null)
        {
            campAnchor = ClampToAreaWalkable(
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Spawn), center, area);
            fieldHomeAnchor = ClampToAreaWalkable(
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Anchor), center, area);
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
                HomeArea = area,
                Position = position,
                Health = stats.MaxHp,
                Alive = true,
                ActivatesAtUtc = now,
                SpawnedAtUtc = now,
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
                // 경계 토출이면 앵커는 경계 안쪽 — 구역이 비어 있으면 이 앵커로 걸어 들어온다.
                AnchorX = fieldHomeAnchor != null ? fieldHomeAnchor.X : position.X,
                AnchorY = fieldHomeAnchor != null ? fieldHomeAnchor.Y : position.Y
            };
            state.Monsters[monster.MonsterId] = monster;
            result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
        }
    }

    /// <summary>
    ///     같은 구역 참가자에게 직선이 뚫려 있는가 — 뚫렸으면 경로를 탈 이유가 없다.
    ///     가장 가까운 한 명만 본다. 전 인원을 훑으면 몹 수백 마리에서 비용이 터진다.
    /// </summary>
    private static bool HasDirectLineToParticipant(
        MonsterRuntime monster, IReadOnlyList<SpotArenaPlayerSpatial> participants)
    {
        var nearest = default(SpotArenaPlayerSpatial);
        float nearestSquared = float.MaxValue;
        bool found = false;
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
            nearest = participant;
            found = true;
        }

        return found && IsSegmentWalkable(monster.Position, nearest.Position);
    }

    /// <summary>
    ///     어그로 반경 안에 같은 구역 참가자가 있는가. 있으면 그 대상으로 개전하고 true.
    ///     보스 (#223): 링 안 = 개전 — 어그로 반경이 곧 사거리(타원 dy×2)라 범위 링이 안전선으로
    ///     정직해진다. 일반 몹은 좁은 접근 반경(2.5) 유지.
    /// </summary>
    /// <summary>
    ///     구역에 남은 사람 중 담당 몹이 가장 적은 사람 (2026-08-16). 주인이 구역을 떠났거나
    ///     탈락했을 때 몹을 넘길 곳을 고른다.
    /// </summary>
    private static long ClaimLeastLoadedOwner(
        MatchState state, AreaType area, IReadOnlyList<SpotArenaPlayerSpatial> participants,
        Func<long, bool>? isOrbless)
    {
        long chosen = 0;
        int least = int.MaxValue;
        bool chosenOrbless = false;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != area)
                continue;

            int load = 0;
            foreach (var candidate in state.Monsters.Values)
            {
                if (candidate.Alive && candidate.OwnerPlayerId == participant.PlayerId)
                    load++;
            }

            // 무오브 우선 (2026-08-16 유저 명세).
            bool orbless = isOrbless?.Invoke(participant.PlayerId) == true;
            if (chosenOrbless && !orbless)
                continue;
            if (orbless && !chosenOrbless)
            {
                chosenOrbless = true;
                least = load;
                chosen = participant.PlayerId;
                continue;
            }

            if (load >= least)
                continue;
            least = load;
            chosen = participant.PlayerId;
        }

        return chosen;
    }

    private static bool HasParticipantWithinAggro(
        MonsterRuntime monster, IReadOnlyList<SpotArenaPlayerSpatial> participants)
    {
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

            // 주인이 있으면 주인만 깨운다 (2026-08-16): 남이 스쳐 지나가는 것만으로
            // 표적이 넘어가면 "각자 할당"이 성립하지 않는다.
            if (monster.OwnerPlayerId != 0 && monster.OwnerPlayerId != participant.PlayerId)
                continue;

            monster.Aggro = true;
            monster.ChaseTargetPlayerId = participant.PlayerId;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     캠프 몹 이동: 잠듦 → (근접·피격·접촉) 어그로 → 리쉬 안 추격 → 이탈 시 앵커 귀환.
    /// </summary>
    private static void UpdateCampMonsterMovement(
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        double deltaSeconds)
    {
        if (!monster.Aggro && !HasParticipantWithinAggro(monster, participants))
            return;

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
        // 공급 몹에는 앵커 리쉬가 없다 (#229 침투): 걸어 들어온 개체가 5.5m 줄에 묶이면 구역
        // 어디에 서든 마주치는 수가 앵커 하나 분량으로 고정돼, 화력을 올려도 잡을 게 늘지 않는다.
        // 구역 경계가 리쉬다 — 위 루프가 이미 같은 구역 참가자만 후보로 본다.
        bool returnToAnchor = !found;
        if (found && !monster.IsSupplyUnit)
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

        // 캠프(레거시)는 포위 없이 직진 — 잠깨는 순간의 접촉 리듬(무적창 테스트)이 이 직진에 기댄다.
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

        // 점유 = 살아있는 참가자가 서 있는 구역. 폐쇄 구역은 즉시 제외한다 — 폐쇄 구역에
        // 쌓인 잔상은 도달조차 못 하면서 전역 상한만 갉아먹는다 (#229 완료 조건 4).
        // 구역별 참가자 명단까지 든다 (2026-08-16): 목표가 인당이라 몇 명이 서 있는지가 곧
        // 목표이고, 스폰한 몹의 주인도 이 명단에서 고른다.
        var occupied = new Dictionary<AreaType, List<long>>();
        foreach (var participant in state.LastParticipants)
        {
            if (participant.Area == AreaType.None ||
                IsAreaClosedResolver?.Invoke(state.MatchingId, participant.Area) == true)
                continue;
            if (!occupied.TryGetValue(participant.Area, out var roster))
            {
                roster = new List<long>();
                occupied[participant.Area] = roster;
            }

            roster.Add(participant.PlayerId);
        }

        bool preMatch = IsGameplayActiveResolver?.Invoke(state.MatchingId) == false;

        // 인트로 산개 (2026-08-16 유저 결정): 매치 시작 전에는 열린 방 전부를 공급 대상으로 본다.
        // 점유 구역만 채우면 발원지에서 나가는 줄기가 플레이어가 선 방 하나뿐이라 "운동장에서
        // 열 방향으로 뻗어 나간다"가 성립하지 않는다. 게이트가 풀리면 점유 규칙으로 돌아가고,
        // 아무도 없는 방의 몹은 좌초 회수가 유예 뒤에 걷는다.
        if (preMatch)
        {
            foreach (var room in MatchSpawnData.GetPhaseRoomCandidates())
            {
                if (IsAreaClosedResolver?.Invoke(state.MatchingId, room) == true)
                    continue;
                occupied.TryAdd(room, new List<long>());
            }
        }

        // 예산 회수 (#229): 비점유·폐쇄 구역은 공급 상태를 버린다. 다시 점유되면 휴지 없이
        // 처음부터 채운다.
        foreach (var zone in state.SupplyZones.Keys.Where(zone => !occupied.ContainsKey(zone)).ToList())
            state.SupplyZones.Remove(zone);

        // 좌초 잔상 회수 (#229 4단계-보정): 잔존 몹까지 걷어내야 예산 회수가 완결된다.
        ReclaimStrandedMonsters(state, occupied, now);

        // 전역 상한은 매 틱 새로 계산한다 — 여러 구역이 같은 틱에 채우면 합계가 넘칠 수 있다.
        int aliveGlobal = CountAliveGlobal(state);
        // 상한은 구역 목표의 합이다 (2026-08-16). 점유 구역 수 × 목표로 잡던 시절에는
        // 사람이 몰려 구역이 줄면 상한도 같이 줄어 남은 전장이 오히려 한산해졌다.
        int globalCap = Math.Min(
            SupplyGlobalAliveHardCap,
            occupied.Values.Sum(roster => GetSupplyZoneTarget(phase.PerPlayerTarget, roster.Count)));

        // 상한에 걸리면 뒤 구역이 굶는다 — 빈 구역부터 채워 공백을 고르게 나눈다.
        foreach (var (zone, roster) in occupied.OrderBy(pair => CountAliveInArea(state, pair.Key)))
        {
            int zoneTarget = GetSupplyZoneTarget(phase.PerPlayerTarget, roster.Count);
            if (aliveGlobal >= globalCap)
                break;

            if (!state.SupplyZones.TryGetValue(zone, out var zoneState))
            {
                zoneState = new SupplyZoneState();
                state.SupplyZones[zone] = zoneState;
            }

            int aliveInZone = CountAliveInArea(state, zone);
            if (aliveInZone >= zoneTarget)
            {
                zoneState.NextTopUpAtUtc = null;
                continue;
            }

            // 전멸 휴지: 한 번이라도 채운 구역이 0이 되면 4초 뒤부터 보충을 재개한다.
            if (aliveInZone == 0 && zoneState.HasSpawned)
            {
                if (zoneState.WipeRestUntilUtc == null)
                {
                    bool finalPhase = phaseIndex == SupplyPhases.Length - 1;
                    zoneState.WipeRestUntilUtc = now.AddSeconds(
                        finalPhase ? SupplyWipeRestSecondsFinalPhase : SupplyWipeRestSeconds);
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
            // 초반 페이즈는 핵을 세우지 않는다 (#229): 시작 오브 하나로는 큰 몹(핵 2.4배)이
            // 벽처럼 서서 파밍이 막힌다. 작은 몹 여럿을 빨리 지우는 리듬이 먼저고,
            // 큰 몹은 오브가 붙기 시작하는 중반부터 나온다.
            bool includeCore = phaseIndex >= SupplyCoreFirstPhaseIndex &&
                               !HasAliveCore(state, zone) &&
                               aliveInZone < zoneTarget &&
                               aliveGlobal < globalCap;
            int room = zoneTarget - aliveInZone - (includeCore ? 1 : 0);
            int want = Math.Min(SupplyTopUpCount, room);
            want = Math.Min(want, globalCap - aliveGlobal - (includeCore ? 1 : 0));
            if (want <= 0 && !includeCore)
                continue;

            want = Math.Max(0, want);
            // 첫 무리도 운동장에서 걸어 들어온다 (2026-08-16 유저 결정). 제자리 스폰으로 초반
            // 공백을 메우려 했지만, 그러면 "운동장에서 각 방으로 나간다"는 그림 자체가 사라진다.
            // 공백은 카운트다운이 메운다 — 게이트 전에도 디렉터가 돌아 5초를 미리 걷는다.
            int spawned = SpawnSupplyMonsters(
                state, zone, want, includeCore, phaseIndex, now, result,
                candidate => IsAreaClosedResolver?.Invoke(state.MatchingId, candidate) == true,
                infiltrate: true,
                roster: roster,
                isOrbless: playerId => IsOrbless(state.MatchingId, playerId));
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

    // 비점유 열린 구역의 잔상을 걷어내기까지의 유예 (#229 4단계-보정): 방을 나서자마자
    // 뒤에서 사라지면 눈에 띈다. 폐쇄 구역은 유예 없이 즉시 걷는다 — 문이 잠겨 도달 불가다.
    private const double StrandedMonsterGraceSeconds = 6d;

    /// <summary>
    ///     좌초 잔상 회수 (#229 4단계-보정). 전역 상한은 하나뿐이라, 아무도 없는 구역에 남은
    ///     잔상이 살아 있는 전장의 몫을 영구히 먹는다. 폐쇄 구역은 문이 잠겨 도달조차 못 하므로
    ///     순수 낭비다 — 폐쇄가 13곳을 닫고 나면 최악에는 전 구역 스폰이 0으로 굳었다.
    ///     원래 설계는 EvacuateArea로 옆 구역에 밀어넣는 것이었으나 그 함수는 호출부가 없는
    ///     죽은 코드였고(주석 세 곳만 그렇게 적고 있었다), 밀어넣기는 받는 구역의 목표 수를
    ///     넘겨 밀도 설계를 흐린다. 그래서 옮기지 않고 회수한다.
    ///     보상은 처치 경로(ApplyMonsterDamage)에만 붙어 있어 이 회수로 소환석이 새지 않는다.
    ///     보스는 애초에 상한에서 제외되므로 건드리지 않는다.
    /// </summary>
    private void ReclaimStrandedMonsters(
        MatchState state, Dictionary<AreaType, List<long>> occupied, DateTime now)
    {
        foreach (var zone in occupied.Keys)
            state.ZoneVacatedAtUtc.Remove(zone);

        foreach (var monster in state.Monsters.Values)
        {
            if (!monster.Alive || IsBossKind(monster.Kind) || occupied.ContainsKey(monster.HomeArea))
                continue;
            // 추격 중인 개체는 남의 구역을 지나는 중이다 — 걷어내면 쫓다 말고 사라진다.
            if (monster.Infiltrating && monster.MarchIsPursuit)
                continue;

            if (IsAreaClosedResolver?.Invoke(state.MatchingId, monster.HomeArea) != true)
            {
                if (!state.ZoneVacatedAtUtc.TryGetValue(monster.HomeArea, out var vacatedAtUtc))
                {
                    state.ZoneVacatedAtUtc[monster.HomeArea] = now;
                    continue;
                }

                if ((now - vacatedAtUtc).TotalSeconds < StrandedMonsterGraceSeconds)
                    continue;
            }

            // 사망 경로를 그대로 쓴다 — 클라가 이미 처리할 줄 알고, PruneDeadMonsters가 치운다.
            monster.Alive = false;
            monster.DiedAtUtc = now;
        }
    }

    /// <summary>전역 생존 몹 수 — 보스(고정 콘텐츠)는 공급 상한에서 제외한다.</summary>
    private static int CountAliveGlobal(MatchState state) =>
        state.Monsters.Values.Count(monster => monster.Alive && !IsBossKind(monster.Kind));

    /// <summary>
    ///     소환석 보상 예산 정산 (#229 4단계). 잡은 몹이 실제로 줄 석을 구역·페이즈 예산에서
    ///     떼어 준다 — 예산이 마르면 0을 돌려주고 몸만 남는다. 스폰이 아니라 처치에 물려야
    ///     "이 구역에서 벌 수 있는 총량"이라는 원래 의도대로 작동한다: 스폰 시 차감은 죽지도
    ///     않은 몹이 예산을 태워, 봇 매치 9690801에서 스폰 1296마리에 석 84개(마리당 0.06)까지
    ///     떨어뜨렸다. 핵은 일반 예산과 섞지 않고 구역·페이즈당 1기까지만 준다 — 핵은 죽을
    ///     때마다 다시 서므로 무제한이면 한 구역에 눌러앉는 것이 최적해가 된다.
    /// </summary>
    private static int ConsumeSupplyStoneBudget(MatchState state, MonsterRuntime monster, DateTime now)
    {
        int reward = monster.SummonStoneReward;
        if (!RegionSupplyModeEnabled || reward <= 0)
            return reward;

        int phaseIndex = GetSupplyPhaseIndex((now - state.StartsAtUtc).TotalSeconds);
        var budgetKey = (monster.HomeArea, phaseIndex);
        if (monster.Kind == SwarmMonsterKind.RunawayGoblin)
            return state.SupplyCoreRewarded.Add(budgetKey) ? reward : 0;

        // 토큰 버킷 (#229 4단계-보정): 고정 풀을 초당 충전으로 바꾼다. 총량은 그대로 두고
        // 분포만 고른다 — 밀도를 4배로 올리자 소진 속도만 4배가 되어 "20초 반짝 뒤 40초 가뭄"이
        // 됐다(매치 9761789: 0~20초 킬당 0.34석 → 40~60초 0.01석). 예산은 "이 구역에서 벌 수
        // 있는 총량"이지 "먼저 죽인 20초가 다 가져간다"가 아니다.
        if (!state.SupplyStoneBucket.TryGetValue(budgetKey, out var bucket))
            bucket = (SupplyStoneBucketBurst, now);

        double refillPerSecond = GetSupplyStoneRefillPerSecond(phaseIndex);
        double elapsedSeconds = Math.Max(0d, (now - bucket.RefilledAtUtc).TotalSeconds);
        double available = Math.Min(
            SupplyStoneBucketBurst, bucket.Available + elapsedSeconds * refillPerSecond);

        int granted = Math.Min(reward, (int)Math.Floor(available));
        state.SupplyStoneBucket[budgetKey] = (available - granted, now);
        return granted;
    }

    // 버킷 상한 (#229 4단계-보정): 마른 뒤 몰아 받는 폭을 제한한다. 낮을수록 촘촘하게 떨어지고
    // 높을수록 뭉쳐 나온다. 5면 가뭄이 최대 몇 초로 끝난다.
    private const double SupplyStoneBucketBurst = 5d;

    /// <summary>
    ///     페이즈 예산을 그 페이즈 길이로 나눈 초당 충전량 (#229 4단계-보정).
    ///     총 지급량은 고정 풀 시절과 같고, 언제 나오는지만 고르게 편다.
    /// </summary>
    private static double GetSupplyStoneRefillPerSecond(int phaseIndex)
    {
        double until = SupplyPhases[phaseIndex].UntilSeconds;
        double from = phaseIndex == 0 ? 0d : SupplyPhases[phaseIndex - 1].UntilSeconds;
        // 마지막 페이즈는 UntilSeconds가 무한이라 매치 잔여로 잡는다.
        double durationSeconds = until > Config.SWARM_MATCH_DURATION_SECONDS
            ? Math.Max(1d, Config.SWARM_MATCH_DURATION_SECONDS - from)
            : Math.Max(1d, until - from);
        return SupplyPhases[phaseIndex].StoneBudget / durationSeconds;
    }

    /// <summary>구역에 살아있는 핵(탈주 고블린)이 있는지 — 핵은 구역당 1기만 유지한다.</summary>
    private static bool HasAliveCore(MatchState state, AreaType area) =>
        state.Monsters.Values.Any(monster =>
            monster.Alive && monster.HomeArea == area && monster.Kind == SwarmMonsterKind.RunawayGoblin);

    // 배정 구역 기준 (#229 침투): 행군 중인 개체도 그 구역의 몫으로 센다 — 물리 위치로 세면
    // 파이프라인에 있는 만큼 디렉터가 한 번 더 채워 상한이 두 배로 부푼다.
    private static int CountAliveInArea(MatchState state, AreaType area) =>
        state.Monsters.Values.Count(monster => monster.Alive && monster.HomeArea == area);

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
        int phaseIndex, DateTime now, SwarmArenaTickResult result,
        Func<AreaType, bool>? isAreaBlocked = null, bool infiltrate = true,
        IReadOnlyList<long>? roster = null, Func<long, bool>? isOrbless = null)
    {
        var phase = SupplyPhases[phaseIndex];
        // 주인 배정 준비 (2026-08-16): 이 구역 사람들의 현재 담당 수를 세어 둔다. 스폰할 때마다
        // 가장 적게 든 사람에게 붙여, 구역 목표(인당 × 인원)가 실제로 균등하게 나뉘게 한다.
        var ownerLoad = new Dictionary<long, int>();
        if (roster is { Count: > 0 })
        {
            foreach (long playerId in roster)
                ownerLoad[playerId] = 0;
            foreach (var candidate in state.Monsters.Values)
            {
                if (!candidate.Alive || candidate.OwnerPlayerId == 0)
                    continue;
                if (ownerLoad.ContainsKey(candidate.OwnerPlayerId))
                    ownerLoad[candidate.OwnerPlayerId]++;
            }
        }

        long ClaimOwner()
        {
            if (ownerLoad.Count == 0)
                return 0;

            // 무오브 우선 (2026-08-16 유저 명세): 무오브는 자동 공격도 절단도 못 하므로,
            // 잔상까지 남을 쫓으면 구석에서 재건하는 동안 아무 압력도 안 받는다.
            long chosen = 0;
            int least = int.MaxValue;
            bool chosenOrbless = false;
            foreach (var (playerId, load) in ownerLoad)
            {
                bool orbless = isOrbless?.Invoke(playerId) == true;
                if (chosenOrbless && !orbless)
                    continue;
                if (orbless && !chosenOrbless)
                {
                    chosenOrbless = true;
                    least = load;
                    chosen = playerId;
                    continue;
                }

                if (load >= least)
                    continue;
                least = load;
                chosen = playerId;
            }

            if (chosen != 0)
                ownerLoad[chosen] = least + 1;
            return chosen;
        }

        // 운동장 밖 구역은 침투로 채운다 — 발원은 운동장 중심, 아래 앵커는 도착지가 된다.
        infiltrate = infiltrate && area != SwarmInwardOriginArea;
        var center = BotPlayerManager.CellToWorldPosition(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        var anchors = new List<Vector3f>(CampsPerArea);
        for (int anchorIndex = 0; anchorIndex < CampsPerArea; anchorIndex++)
        {
            var customAnchorCell = GameMonsterCampData.GetAnchor(area, anchorIndex);
            if (customAnchorCell != null)
            {
                anchors.Add(ClampToAreaWalkable(
                    BotPlayerManager.CellToWorldPosition(
                        Config.SWARM_MATCH_MAP, InsetAnchorFromAreaEdge(customAnchorCell, area)),
                    center, area));
                continue;
            }

            // 절차 폴백: 방 중심 주변 120도 간격 배치.
            float anchorAngle = anchorIndex * 2.0944f;
            anchors.Add(ClampToAreaWalkable(new Vector3f(
                center.X + CampAnchorRadius * 0.6f * MathF.Cos(anchorAngle),
                center.Y + CampAnchorRadius * 0.6f * MathF.Sin(anchorAngle), 0f), center, area));
        }

        // 앵커별 최근접 플레이어 거리 — 안전 이격(2.5m) 미만은 제외하고, 화면 밖(7m)을 우선한다.
        // 같은 조건이면 운동장 쪽 앵커를 먼저 쓴다 (#229 4단계-보정): 잔상이 중앙에서 번져
        // 나오는 것처럼 읽혀야 폐쇄의 방향(바깥 → 중앙)과 정면으로 마주 본다.
        // 침투에서는 이 앵커가 스폰 지점이 아니라 행군 도착지다 — 플레이어 코앞에서 솟는 일이
        // 애초에 없으므로 안전 이격 필터를 걸지 않는다. 전 앵커가 막혀 공급이 멎던 경로도 함께 사라진다.
        var inward = GetInwardDirection(area);
        var ranked = anchors
            .Select(anchor => (
                Anchor: anchor,
                Distance: NearestParticipantDistance(state, area, anchor),
                Inwardness: (anchor.X - center.X) * inward.X + (anchor.Y - center.Y) * inward.Y))
            .Where(entry => infiltrate || entry.Distance >= SupplySafeSpawnDistance)
            .OrderByDescending(entry => entry.Distance >= SupplyOffscreenDistance)
            .ThenByDescending(entry => entry.Inwardness)
            .ThenByDescending(entry => entry.Distance)
            .ToList();
        if (ranked.Count == 0)
            return 0;

        var freeAnchors = ranked.Select(entry => entry.Anchor).ToList();

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
            // 산개도 안쪽으로 한 뼘 민다 — 앵커가 셋뿐이라 편향이 앵커 선택만으로는 약하다.
            var destination = ClampToAreaWalkable(new Vector3f(
                packAnchor.X + MathF.Cos(angle) * SupplyScatterRadius + inward.X * SupplyInwardBias,
                packAnchor.Y + MathF.Sin(angle) * SupplyScatterRadius + inward.Y * SupplyInwardBias,
                0f), packAnchor, area);

            // #272 경계 토출 (2026-08-26 유저 결정): 운동장 발원 침투를 자기장 발원으로 교체 —
            // 잔상은 그 구역의 바깥 띠(경계 관통 중이면 경계 밖 빨간 띠, 아직 안전하면 가장
            // 바깥 띠)에서 태어나 배정 앵커 쪽으로 걸어 들어온다. 리졸버 미주입(자기장 모드
            // 밖)이면 기존 운동장 침투가 폴백이다.
            var position = destination;
            var spawnArea = area;
            List<Vector3f>? route = null;
            var fieldSpawn = FieldSpawnCellResolver?.Invoke(state.MatchingId, area);
            if (fieldSpawn != null)
            {
                position = ClampToAreaWalkable(
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldSpawn.Value.Spawn),
                    packAnchor, area);
                // 도착지도 리졸버의 안쪽 띠 셀로 (#269-A, 2026-08-28 유저 승인): 캠프 앵커
                // 산개점은 복도처럼 좁은 구역에서 스폰 띠와 몇 셀 차이라 "즉시 젠 후 제자리"로
                // 읽혔다 — 구역을 최대로 가로질러 걸어 들어오게 한다. 산개 지터는 유지.
                var fieldAnchorWorld = BotPlayerManager.CellToWorldPosition(
                    Config.SWARM_MATCH_MAP, fieldSpawn.Value.Anchor);
                destination = ClampToAreaWalkable(new Vector3f(
                    fieldAnchorWorld.X + MathF.Cos(angle) * SupplyScatterRadius,
                    fieldAnchorWorld.Y + MathF.Sin(angle) * SupplyScatterRadius,
                    0f), fieldAnchorWorld, area);
            }
            else if (infiltrate &&
                     TryPlanInfiltration(state, area, destination, isAreaBlocked, out var origin,
                         out var planned))
            {
                position = origin;
                spawnArea = SwarmInwardOriginArea;
                route = planned;
            }

            var kind = spawnPlan[index].Kind;
            var stats = GetKindStats(kind);
            bool isCore = kind == SwarmMonsterKind.RunawayGoblin;

            // 보상 표기는 종 기본값을 그대로 단다. 구역·페이즈 예산은 처치 시점에 깎는다
            // (#229 수정): 스폰 때 깎으면 죽지도 않은 몹이 예산을 태워, 페이즈 시작 몇 초 만에
            // 말라붙는다 — 봇 매치 9690801에서 스폰 1296마리에 석 84개(마리당 0.06)까지 떨어졌다.
            int stoneReward = isCore ? SupplyCoreStoneReward : stats.StoneReward;

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
                Area = spawnArea,
                HomeArea = area,
                Position = position,
                Health = maxHp,
                Alive = true,
                // 공급 몹은 잠들지 않는다 (#229): 사냥하러 걸어 들어온 개체가 방에서 다시 잠들면
                // "플레이어가 앵커를 순회하며 깨우는" 예전 구조로 되돌아간다.
                Aggro = true,
                PhaseTier = phaseIndex,
                // 출발 지터 (#232 원형 산개): 같은 틱에 세운 무리가 한 덩어리로 출발하지 않고 파문처럼 번진다.
                ActivatesAtUtc = now.AddSeconds(
                    SupplyTelegraphSeconds + state.Rng.NextDouble() * InfiltrationDepartureJitterSeconds),
                SpawnedAtUtc = now,
                NextContactAtUtc = now,
                ScatterAngle = (float)(state.Rng.NextDouble() * Math.PI * 2d),
                SummonStoneReward = stoneReward,
                // 회복 공급 (2026-08-16 유저 결정: 회복이 수면밖에 없다). 종 기본값으로는
                // 하트가 핵(탈주)에서만 나오고 핵은 페이즈 2부터라, 초중반에 회복 수단이 없다.
                // 일반 몹에 낮은 확률로 얹어 판 내내 흘러나오게 한다 — 상자 탐색이 꺼진 뒤로는
                // 이 경로가 유일한 즉시 회복 공급처다.
                HeartReward = stats.HeartReward > 0
                    ? stats.HeartReward
                    : state.Rng.NextDouble() < SupplyHeartDropChance
                        ? 1
                        : 0,
                BootsReward = stats.BootsReward,
                KeyReward = stats.KeyReward,
                ContactDamageValue = contactDamage,
                Kind = kind,
                MaxHealthValue = maxHp,
                // 파도 문양 원거리 사거리(3.2) 퇴역 (2026-08-24 유저 지시): 접촉으로 때리되
                // 스플래시(WavePatternSplashRadius)가 문양의 정체를 진다. 쿨다운만 문양 값 유지.
                AttackRangeValue = stats.AttackRange,
                AttackCooldownValue = IsWavePatternMonster(pattern) && !isCore
                    ? WavePatternAttackCooldownSeconds
                    : stats.AttackCooldownSeconds,
                // 경계 토출이면 앵커는 방 안쪽 도착지 — 쫓을 대상이 없어도 안쪽으로 걸어 들어온다.
                AnchorX = fieldSpawn != null ? destination.X : position.X,
                AnchorY = fieldSpawn != null ? destination.Y : position.Y,
                // 주인 배정 (2026-08-16 유저 결정): 이 몹은 배정 구역의 특정 한 사람만 쫓는다.
                OwnerPlayerId = ClaimOwner()
            };
            if (route != null)
            {
                monster.Infiltrating = true;
                monster.MarchWaypoints.AddRange(route);
                monster.MarchBudgetSeconds = ComputeMarchBudgetSeconds(position, route);
                // 레인·속도 지터 (#232 원형 산개): 같은 문으로 가는 몹이 한 줄로 포개지지 않는다.
                monster.MarchLaneOffset = (float)(state.Rng.NextDouble() * 2d - 1d) * MarchLaneOffsetMax;
                monster.MarchSpeedScale = 1f + (float)(state.Rng.NextDouble() * 2d - 1d) * MarchSpeedJitter;
            }

            state.Monsters[monster.MonsterId] = monster;
            result.SpawnedMonsters.Add(monster.ToMonsterRuntimeInfo());
        }

        // 공급 계측 (#229): 공급지·페이즈·마릿수·석 보상을 매치 로그로 넘긴다.
        result.SupplyPackSpawns.Add(new SupplyPackSpawnInfo(
            area, phaseIndex, spawnPlan.Count, stoneTotal));
        return spawnPlan.Count;
    }

    /// <summary>
    ///     침투 경로 계획 (#229). 운동장 중심 주변에서 발원점을 잡고 목적지까지의 통로를 미리 깐다.
    ///     폐쇄된 구역은 경로에서 배제한다 — 잠긴 문 앞에 줄을 서면 그대로 상한만 먹는다.
    ///     경로가 없으면 false를 돌려 호출부가 구역 안 스폰으로 되돌아가게 한다.
    /// </summary>
    private static bool TryPlanInfiltration(
        MatchState state, AreaType destinationArea, Vector3f destination,
        Func<AreaType, bool>? isAreaBlocked, out Vector3f origin, out List<Vector3f> route)
    {
        route = null!;
        var originCenter = BotPlayerManager.CellToWorldPosition(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, SwarmInwardOriginArea));

        // 원형 산개 (#232, 2026-08-17 유저 지시 "줄지어서가 아니라 원형으로"): 발원 각도를 무작위가
        // 아니라 "그 방으로 나가는 출구 방향" ± 지터로 잡는다. 목적지가 사방에 있으니 링은 고르게
        // 채워지고, 각자의 첫걸음이 이미 자기 경로 쪽이라 링 안쪽을 되짚어 건너는 줄이 안 생긴다.
        // 방 중심 방향이 아니라 출구 방향인 이유: 행정실처럼 방은 -60°인데 문은 정크장 쪽 -120°인
        // 경우, 방 방향으로 태어난 몹이 곧장 옆으로 꺾여 링을 가로질렀다. 지터는 황금각 수열이라
        // 같은 방으로 가는 연속 스폰끼리도 각도가 겹치지 않는다.
        float baseAngle = ResolveInfiltrationExitBearing(destinationArea, destination, originCenter, isAreaBlocked);
        double golden = (state.NextInfiltrationOriginOrdinal++ * InfiltrationGoldenAngle) % 1d;
        float angle = baseAngle + (float)((golden - 0.5d) * 2d) * InfiltrationOriginJitterRadians;
        origin = ClampToAreaWalkable(new Vector3f(
                originCenter.X + MathF.Cos(angle) * InfiltrationOriginRadius,
                originCenter.Y + MathF.Sin(angle) * InfiltrationOriginRadius, 0f),
            originCenter, SwarmInwardOriginArea);

        // 방사 버스트: 태어난 방향 그대로 한 뼘 더 밀려나는 점을 첫 웨이포인트로 둔다 — 링이 한 번
        // 부풀어 오른 뒤에야 각자 문 쪽으로 꺾인다. BFS 첫 셀이 옆으로 틀어져 있어도 첫 0.5초는 방사다.
        var burst = ClampToAreaWalkable(new Vector3f(
                origin.X + MathF.Cos(angle) * InfiltrationBurstDistance,
                origin.Y + MathF.Sin(angle) * InfiltrationBurstDistance, 0f),
            originCenter, SwarmInwardOriginArea);
        if (IsSegmentWalkable(origin, burst) &&
            TryPlanRoute(SwarmInwardOriginArea, burst, destinationArea, destination,
                candidate => IsInfiltrationRouteBlocked(candidate, destinationArea, isAreaBlocked), out route))
        {
            route.Insert(0, burst);
            return true;
        }

        return TryPlanRoute(
            SwarmInwardOriginArea, origin, destinationArea, destination,
            candidate => IsInfiltrationRouteBlocked(candidate, destinationArea, isAreaBlocked), out route);
    }

    // 목적지별 출구 방위 캐시 (#232 원형 산개): 운동장 중심에서 그 방으로 가는 경로가 발원 링을
    // 벗어나는 지점의 방위. 폐쇄로 경로가 바뀌어도 출구 방향은 거의 같아 매치 간 공유해도 된다.
    private static readonly ConcurrentDictionary<AreaType, float> InfiltrationExitBearings = new();

    private static float ResolveInfiltrationExitBearing(
        AreaType destinationArea, Vector3f destination, Vector3f originCenter, Func<AreaType, bool>? isAreaBlocked)
    {
        if (InfiltrationExitBearings.TryGetValue(destinationArea, out float cached))
            return cached;

        float fallback = MathF.Atan2(destination.Y - originCenter.Y, destination.X - originCenter.X);
        if (!TryPlanRoute(SwarmInwardOriginArea, originCenter, destinationArea, destination,
                candidate => IsInfiltrationRouteBlocked(candidate, destinationArea, isAreaBlocked), out var probe))
            return fallback;

        float exitRadius = InfiltrationOriginRadius + InfiltrationBurstDistance;
        foreach (var point in probe)
        {
            float dx = point.X - originCenter.X;
            float dy = point.Y - originCenter.Y;
            if (dx * dx + dy * dy < exitRadius * exitRadius)
                continue;
            float bearing = MathF.Atan2(dy, dx);
            InfiltrationExitBearings[destinationArea] = bearing;
            return bearing;
        }

        InfiltrationExitBearings[destinationArea] = fallback;
        return fallback;
    }

    /// <summary>
    ///     방을 통로로 쓰지 않는다 (2026-08-16 유저 판정). 구역 그래프에는 방끼리 붙은 간선이
    ///     있어(도서관→교실4, 도서관→창고2, 체육관→교실2 …) BFS가 최단 홉만 보고 방을 관통하는
    ///     경로를 고른다. 그러면 무리가 도서관에 들어갔다가 거기서 갈라지고, 방 안쪽 문에서 막힌다.
    ///     목적지 외의 방을 막으면 남는 길은 운동장·복도·정크장 같은 통로뿐이다 —
    ///     전 방이 그 길로 도달 가능한 것은 확인했다(행정실·교무실·창고2는 정크장 경유).
    /// </summary>
    private static bool IsInfiltrationRouteBlocked(
        AreaType candidate, AreaType destinationArea, Func<AreaType, bool>? isAreaBlocked)
    {
        if (isAreaBlocked?.Invoke(candidate) == true)
            return true;
        if (candidate == destinationArea)
            return false;

        var rooms = MatchSpawnData.GetPhaseRoomCandidates();
        for (int index = 0; index < rooms.Count; index++)
            if (rooms[index] == candidate)
                return true;
        return false;
    }

    /// <summary>
    ///     두 지점 사이 행군 경로. BotPathfinder는 구역 내 BFS가 실패한 구간을 통째로 생략하고
    ///     다음 문어귀 셀로 건너뛰므로, 여기서 전 구간 보행 가능 여부를 확인하고 끊긴 경로는 버린다 —
    ///     그대로 주면 몹이 벽을 뚫고 들어가 방 안쪽 벽에 박힌다 (#229 침투 수리).
    /// </summary>
    private static bool TryPlanRoute(
        AreaType fromArea, Vector3f from, AreaType toArea, Vector3f to,
        Func<AreaType, bool>? isAreaBlocked, out List<Vector3f> route)
    {
        route = null!;
        var steps = BotPathfinder.FindPath(
            Config.SWARM_MATCH_MAP, fromArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, from),
            toArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, to), isAreaBlocked);
        if (steps == null || steps.Count == 0)
            return false;

        var planned = new List<Vector3f>(steps.Count + 2) { from };
        foreach (var step in steps)
            planned.Add(BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, step.Cell));
        planned.Add(to);

        for (int index = 1; index < planned.Count; index++)
            if (!IsSegmentWalkable(planned[index - 1], planned[index]))
                return false;

        planned.RemoveAt(0);
        route = planned;
        return true;
    }

    /// <summary>
    ///     영역 넘김 추격 (2026-08-16 유저 결정). 이미 나를 쫓던 몹은 문을 넘어도 따라온다.
    ///     상대가 구역을 떠나면 그쪽으로 새 경로를 깔아 뒤를 쫓는다 — 구역 경계가 도주선이던
    ///     구조가 사라져, 무리를 달고 다니는 것이 실제 부담이 된다.
    ///     쫓아간 구역이 그 몹의 새 배정 구역이 되므로 공급 회계도 따라 옮겨간다.
    /// </summary>
    private static bool TryStartCrossAreaPursuit(
        MonsterRuntime monster, IReadOnlyList<SpotArenaPlayerSpatial> participants)
    {
        if (monster.ChaseTargetPlayerId == 0 || monster.Infiltrating)
            return false;

        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.PlayerId != monster.ChaseTargetPlayerId ||
                participant.Area == AreaType.None || participant.Area == monster.Area)
                continue;

            if (!TryPlanRoute(monster.Area, monster.Position, participant.Area,
                    participant.Position, null, out var route))
                return false;

            monster.Infiltrating = true;
            monster.MarchIsPursuit = true;
            monster.MarchWaypoints.Clear();
            monster.MarchWaypoints.AddRange(route);
            monster.MarchIndex = 0;
            monster.MarchBudgetSeconds = ComputeMarchBudgetSeconds(monster.Position, route);
            return true;
        }

        return false;
    }

    /// <summary>두 점을 잇는 직선이 전 구간 보행 가능한가 — 셀 반 칸 간격으로 훑는다.</summary>
    private static bool IsSegmentWalkable(Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        int samples = Math.Max(1, (int)MathF.Ceiling(distance / RouteSampleStep));
        int blockedRun = 0;
        for (int index = 1; index <= samples; index++)
        {
            float t = index / (float)samples;
            var point = new Vector3f(from.X + dx * t, from.Y + dy * t, 0f);
            if (GameMapData.IsMoveablePosition(
                    Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, point)))
            {
                blockedRun = 0;
                continue;
            }

            // 문틀은 지난다 (2026-08-16 유저 결정: 몹은 잠긴 문을 무시한다). 문 셀은 정적
            // 지도에서 비보행이라, 한 칸도 허용 안 하면 방으로 가는 경로가 통째로 폐기되고
            // 침투가 제자리 스폰으로 떨어진다. 벽을 가로지르는 긴 구간만 거른다.
            if (++blockedRun > DoorwayBlockedSampleTolerance)
                return false;
        }

        return true;
    }

    /// <summary>
    ///     침투 행군 한 틱. 경로를 따라 걷고 물리 구역을 갱신한다 — 행군 중에는 운동장·복도
    ///     소속이라 그 구역에 선 누구에게든 보이고 맞는다.
    ///     도착 판정은 물리 구역이 한다: 문턱을 넘는 순간 사냥이 시작된다.
    ///     제한시간을 넘겨도 배정 구역에 못 들어갔으면 걷어낸다 — 복도에 낀 개체가 배정 구역의
    ///     목표 수를 영구히 차지하면 그 구역 공급이 그대로 멎는다.
    /// </summary>
    private static void AdvanceInfiltration(
        MonsterRuntime monster, double deltaSeconds, DateTime now, bool holdAtThreshold = false)
    {
        // 인트로 예열: 배정 구역 밖에서 멈춘다 (2026-08-16 유저 판정).
        // "운동장에서 흩어지는 것이 시작"인데 카운트다운이 끝나기도 전에 방 안에 몹이 서 있으면
        // 어디서 왔는지가 안 읽힌다. 방에 들어간 뒤 멈추면 플레이어 코앞에 뭉치기까지 한다 —
        // 문턱을 넘기 직전에 세워, 매치가 열리는 순간 문을 통해 들이닥치게 한다.
        if (holdAtThreshold && monster.Area == monster.HomeArea)
            return;

        monster.MarchBudgetSeconds -= deltaSeconds;
        float remaining = (float)(MonsterMoveSpeed * monster.MarchSpeedScale *
                                  GetMonsterWaveSlowMultiplier(monster) * deltaSeconds);
        // 경로는 셀 단위라 한 틱에 웨이포인트를 여러 개 지난다. 남은 이동량을 다 쓸 때까지 돈다.
        while (remaining > 0f && monster.MarchIndex < monster.MarchWaypoints.Count)
        {
            var waypoint = ResolveLaneWaypoint(monster);
            float dx = waypoint.X - monster.Position.X;
            float dy = waypoint.Y - monster.Position.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= MarchWaypointArriveDistance)
            {
                monster.MarchIndex++;
                continue;
            }

            float step = Math.Min(remaining, distance);
            var proposed = new Vector3f(
                monster.Position.X + dx / distance * step,
                monster.Position.Y + dy / distance * step, 0f);

            // 예열 중에는 문턱을 넘지 않는다 — 한 발 앞이 배정 구역이면 거기서 선다.
            if (holdAtThreshold &&
                GameMapData.GetCurrentArea(
                    Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed)) ==
                monster.HomeArea)
                return;

            // 문틀은 밀고 지나되 벽 안에 멈추지는 않는다 (2026-08-16 유저 판정: 모서리에 끼는
            // 몹이 많다). 벽 판정을 통째로 걷었더니 직선 이동이 구조물 안으로 파고들었고,
            // 도착 후 충돌 판정이 있는 추격 이동이 그 자리에 몹을 붙여 놓았다.
            // 비보행 칸을 밟는 것은 목표 웨이포인트가 보행 가능할 때만 허용한다 — 그러면
            // 다음 틱에 반드시 빠져나온다. 웨이포인트 자체가 구조물 안이면 그 점을 버린다.
            if (!GameMapData.IsMoveablePosition(
                    Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed)) &&
                !GameMapData.IsMoveablePosition(
                    Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, waypoint)))
            {
                monster.MarchIndex++;
                continue;
            }

            monster.Position = proposed;
            remaining -= step;
        }

        monster.Area = GameMapData.GetCurrentArea(
            Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position));

        // 도착은 문턱을 넘는 순간이다. "경로를 다 걸어야 도착"으로 바꿨더니 제한시간에 걸린
        // 개체가 방 밖에서 대량으로 걷혀 방 처치가 0이 됐다(봇 매치 9866042, 운동장 비중 100%).
        // 문 앞에 서는 문제는 도착 시점이 아니라 앵커 위치가 원인이므로 그쪽에서 푼다 —
        // ArriveFromInfiltration이 경로의 마지막 점(배정된 캠프 앵커)을 앵커로 잡는다.
        if (monster.Area == monster.HomeArea)
        {
            if (!holdAtThreshold)
                ArriveFromInfiltration(monster);
            return;
        }

        // 경로를 다 썼는데도 배정 구역 밖이면 벽에 막힌 것이다. 그 자리에 세워 두면 문 앞이
        // 아니라 벽에 몹이 쌓이므로 걷어낸다 — 디렉터가 다음 보충에서 다시 보낸다.
        // 예열 중에는 걷어내지 않는다: 아직 판이 열리지도 않았는데 줄이 사라지면 안 된다.
        if (holdAtThreshold ||
            (monster.MarchIndex < monster.MarchWaypoints.Count && monster.MarchBudgetSeconds > 0d))
            return;

        // 추격은 사라지지 않는다 — 못 따라잡으면 그 자리에서 멈춰 다시 주변을 사냥한다.
        if (monster.MarchIsPursuit)
        {
            ArriveFromInfiltration(monster);
            return;
        }

        monster.Alive = false;
        monster.DiedAtUtc = now;
    }

    // 추격 경로 판정 주기 (2026-08-16). 직선이 뚫렸는지 확인하는 데 전 구간 샘플링이 들어가므로
    // 개체마다 이 간격으로만 다시 본다 — 매 틱 돌리면 몹 수백 마리에서 비용이 터진다.
    private const double ChasePlanIntervalSeconds = 0.4d;

    // 정지 감시 (2026-08-16): 8초 이상 제자리인 개체를 한 번 보고한다. "구석에 껴서 아무것도
    // 안 하는 몹" 류는 원인이 여러 층(경로·충돌·앵커·맵 데이터)에 걸쳐 있어 추측으로는 안 잡힌다.
    // 봇 매치 로그에서 이 줄이 0인지만 보면 회귀를 즉시 안다.
    private const double StuckReportSeconds = 8d;
    private const float StuckMoveThresholdSquared = 0.04f;

    /// <summary>후류 소용돌이 감속 (#268): 봇(GetBotWaveSlowMultiplier)과 같은 값·같은 시계.</summary>
    private static float GetMonsterWaveSlowMultiplier(MonsterRuntime monster)
    {
        return DateTime.UtcNow < monster.WaveSlowUntilUtc
            ? OrbData.WaveSlowMoveSpeedMultiplier
            : 1f;
    }

    private static void TrackStuckMonster(
        MonsterRuntime monster, DateTime now, SwarmArenaTickResult result)
    {
        float dx = monster.Position.X - monster.StuckWatchX;
        float dy = monster.Position.Y - monster.StuckWatchY;
        if (dx * dx + dy * dy > StuckMoveThresholdSquared)
        {
            monster.StuckWatchX = monster.Position.X;
            monster.StuckWatchY = monster.Position.Y;
            monster.StuckSinceUtc = now;
            monster.StuckReported = false;
            return;
        }

        if (monster.StuckReported || (now - monster.StuckSinceUtc).TotalSeconds < StuckReportSeconds)
            return;

        monster.StuckReported = true;
        result.StuckReports.Add(
            $"stuck monster={monster.MonsterId} kind={monster.Kind} area={monster.Area} " +
            $"home={monster.HomeArea} infiltrating={monster.Infiltrating} aggro={monster.Aggro} " +
            $"chase={monster.ChaseTargetPlayerId} marchIndex={monster.MarchIndex}/{monster.MarchWaypoints.Count} " +
            $"budget={monster.MarchBudgetSeconds:F1} pos=({monster.Position.X:F1},{monster.Position.Y:F1}) " +
            $"anchor=({monster.AnchorX:F1},{monster.AnchorY:F1})");
    }

    /// <summary>
    ///     비보행 칸에 선 몹을 보행 가능한 자리로 당긴다. 구역 중심 쪽으로 당기되, 구역 판정이
    ///     서지 않으면(문틀·경계) 전역 보행 기준으로 되돌린다.
    /// </summary>
    private static void RescueMonsterFromBlockedCell(MonsterRuntime monster)
    {
        // 행군 중에는 건드리지 않는다 (2026-08-16 수리): 침투는 문틀(비보행 셀)을 일부러
        // 밟고 지나는데, 여기서 매 틱 구역 중심으로 당기면 문을 영영 못 넘는다 —
        // 실측에서 배정 구역이 아닌 몹 580마리가 운동장에서 죽었다(매치 9857526).
        if (monster.Infiltrating)
            return;

        if (GameMapData.IsMoveablePosition(
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position)))
            return;

        var areaCenter = BotPlayerManager.CellToWorldPosition(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monster.Area));
        var rescued = ClampToAreaWalkable(monster.Position, areaCenter, monster.Area);
        if (!GameMapData.IsMoveablePosition(
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, rescued)))
            rescued = ClampToWalkable(monster.Position, areaCenter);

        monster.Position = rescued;
    }

    /// <summary>
    ///     레인 웨이포인트 (#232 원형 산개): 현재 웨이포인트를 진행 방향의 수직으로 몹별 오프셋만큼
    ///     비켜 잡는다. 마지막 점(배정 앵커)은 그대로 — 산개 반경이 이미 흩어 두었다. 비켜 잡은
    ///     칸이 벽이면(문어귀·모서리) 원래 점을 쓴다 — 레인은 넓은 곳에서만 살고 좁은 곳에서는 접힌다.
    /// </summary>
    private static Vector3f ResolveLaneWaypoint(MonsterRuntime monster)
    {
        var waypoint = monster.MarchWaypoints[monster.MarchIndex];
        if (MathF.Abs(monster.MarchLaneOffset) < 0.01f ||
            monster.MarchIndex >= monster.MarchWaypoints.Count - 1)
            return waypoint;

        var from = monster.MarchIndex == 0 ? monster.Position : monster.MarchWaypoints[monster.MarchIndex - 1];
        float dx = waypoint.X - from.X;
        float dy = waypoint.Y - from.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.05f)
            return waypoint;

        var offset = new Vector3f(
            waypoint.X - dy / length * monster.MarchLaneOffset,
            waypoint.Y + dx / length * monster.MarchLaneOffset, 0f);
        return GameMapData.IsMoveablePosition(
            Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, offset))
            ? offset
            : waypoint;
    }

    /// <summary>침투 종료 — 도착 지점을 앵커로 삼고 그대로 교전에 들어간다.</summary>
    private static void ArriveFromInfiltration(MonsterRuntime monster)
    {
        // 문틀을 밟은 채로 도착할 수 있다. 그대로 두면 충돌 판정이 있는 추격 이동이 갇히므로
        // 보행 가능한 자리로 당겨 놓는다 (2026-08-16).
        if (!GameMapData.IsMoveablePosition(
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, monster.Position)))
        {
            var areaCenter = BotPlayerManager.CellToWorldPosition(
                Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, monster.Area));
            monster.Position = ClampToAreaWalkable(monster.Position, areaCenter, monster.Area);
        }

        // 앵커는 경로의 마지막 점 — 배정된 캠프 앵커다 (2026-08-16 유저 제보: 몹이 방에
        // 안 들어가고 문 앞에 서 있다). 도착 지점(문턱)을 앵커로 잡으면, 방에 아무도 없을 때
        // 몹이 문간으로 되돌아가 선다. 방 안쪽 앵커를 주면 그리로 걸어 들어간다.
        // 추격 행군은 목표가 사람이라 그 좌표를 앵커로 삼지 않는다 — 선 자리를 그대로 쓴다.
        if (!monster.MarchIsPursuit && monster.MarchWaypoints.Count > 0)
        {
            var destination = monster.MarchWaypoints[^1];
            monster.AnchorX = destination.X;
            monster.AnchorY = destination.Y;
        }
        else
        {
            monster.AnchorX = monster.Position.X;
            monster.AnchorY = monster.Position.Y;
        }

        // 쫓아간 구역이 새 배정 구역이 된다 (2026-08-16 결정의 미구현분, 2026-08-28 수리):
        // HomeArea가 옛 방에 남으면 그 방이 비는 순간 좌초 회수가 추격 도착분까지 걷어가
        // "문을 넘어 따라온 몹이 몇 초 뒤 증발"했다. 공급 회계도 주석대로 함께 옮겨간다.
        if (monster.MarchIsPursuit)
            monster.HomeArea = monster.Area;

        monster.Infiltrating = false;
        monster.MarchIsPursuit = false;
        monster.MarchWaypoints.Clear();
        monster.MarchIndex = 0;
        monster.Aggro = true;
    }

    /// <summary>
    ///     구역 중심에서 운동장(최종 폐쇄 구역) 쪽으로 향하는 단위 벡터 (#229 4단계-보정).
    ///     폐쇄는 먼 방부터 닫혀 플레이어를 중앙으로 민다. 잔상이 그 반대편에서 나오면 두 흐름이
    ///     엇갈려 읽히므로, 잔상은 플레이어가 밀려갈 방향에서 나오게 한다.
    ///     운동장 자신은 발원지라 편향이 없다.
    /// </summary>
    private static Vector3f GetInwardDirection(AreaType area)
    {
        if (area == SwarmInwardOriginArea)
            return new Vector3f(0f, 0f, 0f);

        var center = BotPlayerManager.CellToWorldPosition(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
        var origin = BotPlayerManager.CellToWorldPosition(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, SwarmInwardOriginArea));
        float dx = origin.X - center.X;
        float dy = origin.Y - center.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        return length < 0.001f
            ? new Vector3f(0f, 0f, 0f)
            : new Vector3f(dx / length, dy / length, 0f);
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
        MatchState state,
        MonsterRuntime monster,
        IReadOnlyList<SpotArenaPlayerSpatial> participants,
        DateTime now,
        double deltaSeconds,
        bool holdAtThreshold = false,
        Func<long, bool>? isOrbless = null)
    {
        // 침투 행군 (#229): 배정 구역에 닿기 전까지는 경로를 따라 걷는다. 도중에 어그로 반경 안
        // 참가자를 만나면 거기서 멈추고 붙는다 — 지나는 길목이 곧 전장이다.
        if (monster.Infiltrating)
        {
            // 행군에서 빠져나오는 조건 (2026-08-16 계측 수리).
            // 예열 중이면 무조건 행군 — 문턱 밖에서 기다리는 것이 연출이다.
            // 그 밖에는 (a) 어그로 반경 안에 누가 있거나, (b) 같은 구역 참가자에게 직선이
            // 뚫렸으면 행군을 접고 추격으로 넘어간다.
            // (b)가 없으면 추격 재계획이 몹을 계속 행군 상태로 되돌리고, 그 상태는 2.5m
            // 안에 들어와야만 풀리므로 몹이 영영 붙지 못한다 — 봇 매치 9864125에서
            // 어그로 36/36인데 추격 대상은 10/36, 최근접 3.98, 접촉 0이었다.
            // 직선 탈출은 추격 행군에만 준다 (2026-08-16 수리). 공급 행군에까지 주면
            // 운동장을 지나던 몹이 거기 있는 봇을 보고 그 자리에 눌러앉아 배정된 방까지
            // 가지 않는다 — 봇 매치 9866292에서 운동장 처치 비중 100%, 방 처치 0이 됐다.
            // 공급 행군은 어그로 반경(2.5m)에 들어와야만 멈춘다: 지나가다 부딪히면 싸우고,
            // 멀리 보이는 것에는 흔들리지 않는다.
            bool leaveMarch = !holdAtThreshold &&
                              (HasParticipantWithinAggro(monster, participants) ||
                               (monster.MarchIsPursuit &&
                                HasDirectLineToParticipant(monster, participants)));
            if (!leaveMarch)
            {
                AdvanceInfiltration(monster, deltaSeconds, now, holdAtThreshold);
                return;
            }

            ArriveFromInfiltration(monster);
        }

        if (!monster.Aggro && !HasParticipantWithinAggro(monster, participants))
            return;

        // 추격 대상 = 주인 (2026-08-16 유저 결정: 플레이어별로 추격 몹이 각자 할당되고
        // 그 몹만 쫓는다). 예전에는 1초마다 같은 구역 최근접을 다시 골랐다 — 그러면 한 사람이
        // 지나갈 때마다 무리가 통째로 그쪽으로 쏠려, 구역 목표를 인당으로 잡아 둔 몫이
        // 실제로는 한 사람에게 몰렸다.
        bool found = false;
        var target = default(SpotArenaPlayerSpatial);

        // 근접 난입 (2026-08-28 플레이 제보 "복도 몹들이 나를 무시하고 대기"): 인당 할당
        // 원칙(주인만 쫓는다)은 유지하되, 남이라도 어그로 반경 안까지 들어오면 그 순간의
        // 표적이 된다 — 좁은 복도에서 남의 몫 몹 무리가 코앞 사람을 무시하고 서 있는
        // "장식 몹"을 없앤다. 반경을 벗어나면 다음 표적 판정에서 주인 추격으로 돌아간다.
        float intruderNearestSquared = CampAggroRadius * CampAggroRadius;
        for (int index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area)
                continue;
            float intruderDx = participant.Position.X - monster.Position.X;
            float intruderDy = participant.Position.Y - monster.Position.Y;
            float intruderSquared = intruderDx * intruderDx + intruderDy * intruderDy;
            if (intruderSquared >= intruderNearestSquared)
                continue;
            intruderNearestSquared = intruderSquared;
            target = participant;
            found = true;
        }

        long chaseId = monster.OwnerPlayerId != 0 ? monster.OwnerPlayerId : monster.ChaseTargetPlayerId;
        for (int index = 0; index < participants.Count && !found && chaseId != 0; index++)
        {
            var participant = participants[index];
            if (participant.Area != monster.Area || participant.PlayerId != chaseId)
                continue;
            target = participant;
            found = true;
            break;
        }

        // 주인이 이 구역에 없으면 재배정한다 — 나갔거나 탈락했다. 남은 사람 중 담당이 가장
        // 적은 쪽으로 넘겨야 한 사람에게 두 몫이 쌓이지 않는다.
        if (!found && monster.OwnerPlayerId != 0)
        {
            long departedOwnerId = monster.OwnerPlayerId;
            monster.OwnerPlayerId = 0;
            long reassigned = ClaimLeastLoadedOwner(state, monster.Area, participants, isOrbless);
            if (reassigned != 0)
            {
                monster.OwnerPlayerId = reassigned;
                for (int index = 0; index < participants.Count; index++)
                {
                    if (participants[index].PlayerId != reassigned)
                        continue;
                    target = participants[index];
                    found = true;
                    break;
                }
            }
            else if (monster.Aggro)
            {
                // 문 너머 추격 수리 (2026-08-28 플레이 제보 "몹이 문 너머로 안 따라온다"):
                // 주인 추격은 ChaseTargetPlayerId에 남지 않아 아래 TryStartCrossAreaPursuit가
                // 평시 주인 몹에게 한 번도 발동하지 않았고, 구역이 비면 6초 뒤 좌초 회수가
                // 몹을 통째로 걷어갔다. 구역이 통째로 비었을 때만 떠난 주인을 추격 표적으로
                // 승격한다 — 남은 사람이 있으면 재배정(구역 몹은 구역 사람 몫)이 우선이다.
                monster.ChaseTargetPlayerId = departedOwnerId;
            }
        }

        // 주인 없는 구형 개체(캠프·보스)는 종전대로 같은 구역 최근접을 쫓는다.
        if (!found && monster.OwnerPlayerId == 0)
        {
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

        // 같은 구역에서 놓쳤다면 문 너머로 쫓는다. 실패해야 앵커로 물러선다.
        if (!found && TryStartCrossAreaPursuit(monster, participants))
        {
            AdvanceInfiltration(monster, deltaSeconds, now);
            return;
        }

        if (!found)
        {
            // 구역에 아무도 없다 — 앵커로 물러서되 서 있지 않는다 (#269-B, 2026-08-28 유저
            // 승인 "정지 대신 순찰"): 앵커 주변을 시간 위상 원운동으로 배회한다 — 주인을 잃고
            // 얼어붙은 몹이 "장식"으로 읽히던 것을 없애고, 지나는 사람 눈에 살아 있는 위협으로
            // 남는다. 다시 잠들지는 않는다 (#229 침투).
            var anchor = new Vector3f(monster.AnchorX, monster.AnchorY, 0f);
            float homeDx = anchor.X - monster.Position.X;
            float homeDy = anchor.Y - monster.Position.Y;
            if (homeDx * homeDx + homeDy * homeDy <=
                SupplyIdlePatrolRadius * SupplyIdlePatrolRadius * 4f)
            {
                monster.ChaseTargetPlayerId = 0;
                double patrolSeconds = (now - monster.SpawnedAtUtc).TotalSeconds;
                float patrolAngle = monster.ScatterAngle +
                                    (float)(patrolSeconds * SupplyIdlePatrolAngularSpeed);
                var patrolPoint = new Vector3f(
                    anchor.X + MathF.Cos(patrolAngle) * SupplyIdlePatrolRadius,
                    anchor.Y + MathF.Sin(patrolAngle) * SupplyIdlePatrolRadius, 0f);
                if (!GameMapData.IsMoveablePosition(
                        Config.SWARM_MATCH_MAP,
                        MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, patrolPoint)))
                    patrolPoint = anchor;
                MoveTowardPlayer(monster, patrolPoint, deltaSeconds);
                return;
            }

            MoveTowardPlayer(monster, anchor, deltaSeconds);
            return;
        }

        monster.ChaseTargetPlayerId = target.PlayerId;
        if (monster.AttackRangeValue > ContactRange)
        {
            // 정지 판정도 공격 판정과 같은 타원(dy×2)으로 잰다 (2026-08-24 유저 제보 "접근 다
            // 안 했는데 멈춰 있다": 평면 원으로 재면 세로 접근 개체가 타원 사거리(dy ≤ 사거리/2)
            // 밖에서 멈춰 영영 공격을 못 하고 서 있었다).
            float holdDx = target.Position.X - monster.Position.X;
            float holdDy = (target.Position.Y - monster.Position.Y) * 2f;
            float holdRange = monster.AttackRangeValue * RangedHoldRangeRatio;
            if (holdDx * holdDx + holdDy * holdDy <= holdRange * holdRange)
                return;
        }

        // 추격 이동 선택 (2026-08-16 유저 결정: 경로탐색을 적용한다).
        // (아래 직선 판정은 포위 오프셋 이전의 본체 좌표 기준 — 오프셋 목표가 벽이면
        //  MoveTowardPlayer의 축별 미끄러짐이 처리한다.)
        // 직선이 뚫려 있으면 조향으로 쫓는다 — 반응이 빠르고 무리가 자연스럽게 퍼진다.
        // 막혀 있으면 그 자리에서 바로 경로를 깐다. 막힌 뒤에 뒤늦게 전환하면 그 사이 벽에
        // 붙어 미끄러지는 구간이 눈에 남는다("타일 끝에 껴 있는 몹").
        // 판정은 개체마다 0.4초에 한 번만 — 매 틱 전 구간을 훑으면 몹 수백 마리에서 비용이 터진다.
        if (now >= monster.NextChasePlanAtUtc)
        {
            monster.NextChasePlanAtUtc = now.AddSeconds(ChasePlanIntervalSeconds);
            bool direct = IsSegmentWalkable(monster.Position, target.Position);
            if (!direct &&
                TryPlanRoute(monster.Area, monster.Position, target.Area, target.Position,
                    null, out var detour))
            {
                // 배정 구역은 그대로 둔다 (2026-08-16): 추격할 때마다 HomeArea를 목표 구역으로
                // 옮기면 공급 회계가 사람이 몰린 구역으로 쏠린다 — 실측에서 운동장 처치 비중이
                // 64% -> 80%로 올랐다. 좌초 회수는 추격 중인 개체를 건드리지 않는 것으로 푼다.
                monster.Infiltrating = true;
                monster.MarchIsPursuit = true;
                monster.MarchWaypoints.Clear();
                monster.MarchWaypoints.AddRange(detour);
                monster.MarchIndex = 0;
                monster.MarchBudgetSeconds = ComputeMarchBudgetSeconds(monster.Position, detour);
                return;
            }
        }

        MoveTowardPlayer(monster, target.Position, deltaSeconds, surround: SurroundEnabled);
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

        // 폐쇄 구역은 신규 스폰 정지 — 잔존 몹은 ReclaimStrandedMonsters가 걷어낸다.
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
            HomeArea = anchor.Area,
            Position = position,
            Health = MonsterMaxHealth,
            Alive = true,
            ActivatesAtUtc = now.AddSeconds(telegraphSeconds),
            SpawnedAtUtc = now,
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

    private static void MoveTowardPlayer(
        MonsterRuntime monster, Vector3f playerPosition, double deltaSeconds, bool surround = false)
    {
        // 포위 오프셋 (#232): 개체 고유 각도의 접근 목표 — 사방에서 조여드는 흩어진 고리.
        // 반경은 추격하는 동안만 줄어들고, 현재 거리로 캡한다 — 이미 붙은 몹이 오프셋 지점으로
        // 멀어지면 접촉이 영영 안 난다. 앵커 복귀(surround=false)는 오프셋 없이 정확히 간다.
        float radius = 0f;
        // 플레이어와 같은 구역에 들어온 뒤부터 포위한다 — 문·복도의 몹이 오프셋 각도 때문에
        // 문을 못 찾고 겉돌면 구역 목표 수가 영영 안 찬다. 밖에서는 오프셋 없이 직진.
        var playerArea = GameMapData.GetCurrentArea(
            Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, playerPosition));
        if (surround && monster.Area == playerArea)
        {
            // 숨쉬는 포위 (#232): 반경이 0에 머물면 전원이 플레이어 위로 수렴한다 — 그게
            // "뭉쳐 따라옴"이다. 사이클을 돈다: 고리 접근 → 조임 → 반경 0 구간(접촉 창, 음수로
            // 계속 감쇠) → 시작 반경으로 리셋해 다시 걸어 나온다. 리셋 폭을 개체 각도로 흔들어
            // 위상이 갈라진다. 거리 캡은 두지 않는다 — 붙은 몹이 min(반경, 거리 0)에 갇혀
            // 안 움직이면 정지 감시가 걷어가 구역 수가 출렁였다(실측).
            monster.SurroundRadius -= (float)(SurroundShrinkPerSecond * deltaSeconds);
            if (monster.SurroundRadius <= -SurroundLungeDepth)
                monster.SurroundRadius = SurroundStartRadius *
                                         (0.7f + 0.3f * (MathF.Sin(monster.ScatterAngle * 3.7f) + 1f) * 0.5f);
            radius = MathF.Max(0f, monster.SurroundRadius);
        }

        var target = new Vector3f(
            playerPosition.X + MathF.Cos(monster.ScatterAngle) * radius,
            playerPosition.Y + MathF.Sin(monster.ScatterAngle) * radius * 0.5f,
            0f);
        // 좁은 방에서는 오프셋 목표가 방 밖(복도)으로 새 구역 인원이 흘러나간다 —
        // 플레이어가 선 구역 안으로 눌러 담는다 (밖이면 플레이어 쪽으로 줄인다).
        if (radius > 0.05f && playerArea != AreaType.None)
            target = ClampToAreaWalkable(target, playerPosition, playerArea);
        float dx = target.X - monster.Position.X;
        float dy = target.Y - monster.Position.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance < 0.05f)
            return;

        float step = (float)(MonsterMoveSpeed * GetMonsterWaveSlowMultiplier(monster) * deltaSeconds);
        if (step > distance)
            step = distance;
        var proposed = new Vector3f(
            monster.Position.X + dx / distance * step,
            monster.Position.Y + dy / distance * step,
            0f);

        Cell proposedCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed);
        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, proposedCell))
        {
            monster.Position = proposed;
            return;
        }

        // 벽이면 축별로 미끄러진다.
        var slideX = new Vector3f(proposed.X, monster.Position.Y, 0f);
        if (GameMapData.IsMoveablePosition(
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, slideX)))
        {
            monster.Position = slideX;
            return;
        }

        var slideY = new Vector3f(monster.Position.X, proposed.Y, 0f);
        if (GameMapData.IsMoveablePosition(
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, slideY)))
            monster.Position = slideY;
    }

    /// <summary>
    ///     방 벽에서 최소 여유 (#229). 구역 박스의 테두리 = 벽선인데 map_region.csv에는 방 둘레가
    ///     obstacle로 적혀 있지 않다(행정실은 16×20 방에 obstacle 2줄뿐). 그래서 테두리 셀이
    ///     "통행 가능"으로 통과하고, 거기서 태어난 잔상이 벽에 낀 채로 선다.
    ///     저작 데이터를 고쳐도 다음에 앵커를 옮기면 또 나므로 코드에서 막는다.
    /// </summary>
    private const int AnchorAreaEdgeMargin = 3;

    /// <summary>
    ///     앵커를 구역 박스 안쪽으로 민다 (#229). 정상 저작된 방은 여유가 3~6칸이라
    ///     이 보정이 아무 일도 하지 않는다 — 경계에 붙은 앵커만 걸린다.
    /// </summary>
    public static Cell InsetAnchorFromAreaEdge(Cell cell, AreaType area)
    {
        var region = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .FirstOrDefault(candidate => candidate.AreaType == area);
        if (region == null)
            return cell;

        // 방이 여유의 두 배보다 좁으면 밀 자리가 없다 — 중앙만 남기고 포기한다.
        int minX = region.Start.X + AnchorAreaEdgeMargin;
        int maxX = region.End.X - AnchorAreaEdgeMargin;
        int minY = region.Start.Y + AnchorAreaEdgeMargin;
        int maxY = region.End.Y - AnchorAreaEdgeMargin;
        if (minX > maxX || minY > maxY)
            return cell;

        int insetX = Math.Clamp(cell.X, minX, maxX);
        int insetY = Math.Clamp(cell.Y, minY, maxY);
        return insetX == cell.X && insetY == cell.Y ? cell : new Cell(insetX, insetY);
    }

    private static bool IsWalkableInArea(Vector3f position, AreaType area)
    {
        Cell cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position);
        return GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) &&
               GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == area;
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
                Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position)))
            return position;

        // 스폰 위치가 보행 불가면 기준점 쪽으로 당기며 첫 보행 가능 지점을 찾는다.
        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            var candidate = new Vector3f(
                position.X + (center.X - position.X) * t,
                position.Y + (center.Y - position.Y) * t,
                0f);
            if (GameMapData.IsMoveablePosition(
                    Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate)))
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

        // 구역이 빈 시각 (#229 4단계-보정): 좌초 잔상 회수 유예를 재는 기준. 다시 점유되면 지운다.
        public Dictionary<AreaType, DateTime> ZoneVacatedAtUtc { get; } = new();

        // 소환석 토큰 버킷 (#229 4단계-보정): 구역·페이즈별 (잔량, 마지막 충전 시각).
        // 구역을 비웠다 돌아와도 살아남는다 — 들락날락으로 리셋되면 보상이 무제한이 된다.
        // 고정 풀에서 초당 충전으로 바뀌었다: 총량은 같고 분포만 고르다.
        public Dictionary<(AreaType Area, int PhaseIndex), (double Available, DateTime RefilledAtUtc)>
            SupplyStoneBucket
        { get; } = new();

        // 핵 보상 정산 (#229 4단계): 석을 준 (구역, 페이즈) 조합 — 같은 칸에서 두 번째 핵부터는 몸만.
        public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
        public DateTime ContactProbeAtUtc { get; set; }
        public int NextSupplyPackOrdinal { get; set; }
        // 침투 발원 순번 (#232 원형 산개): 황금각 수열의 인덱스 — 연속 스폰의 각도 지터가 고르게 퍼진다.
        public int NextInfiltrationOriginOrdinal { get; set; }
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

        // 기준점 계측 (#232 1단계): 스폰 시각과 살아 있는 동안 받은 오브 공격 사건 수.
        // 완료 조건 "한 몬스터가 살아 있는 동안 평균 2회 이상의 공격 모양"의 근거다.
        // 즉시 사라지는 몹은 교차사격 기준점이 못 된다 — 종별 생존시간을 이 둘로 잰다.
        public DateTime SpawnedAtUtc { get; set; }
        public int AttackEventCount { get; set; }
        public float ScatterAngle { get; init; }

        // 후류 소용돌이 감속 (#268): 당김 직후 잠깐 늦는다 — 봇 WaveSlowUntilUtc와 대칭.
        public DateTime WaveSlowUntilUtc { get; set; }

        // 포위 반경 (#232): 추격 중 매 틱 줄어든다. 스폰 시 SurroundStartRadius로 시작.
        public float SurroundRadius { get; set; } = SurroundStartRadius;
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

        // 침투 (#229): 공급 몹은 운동장 중심에서 태어나 배정 구역까지 행군한 뒤 사냥에 들어간다.
        // HomeArea = 배정 구역. 공급 회계(구역 목표·석 예산·좌초 회수)는 전부 이 값을 본다 —
        // 행군 중인 개체가 목표 수에서 빠지면 디렉터가 파이프라인을 두 번 채워 폭주한다.
        // Area는 물리 위치의 구역이라 행군 중에는 운동장·복도로 바뀐다 (클라 컬링·전투 판정 기준).
        public AreaType HomeArea { get; set; }

        // 스폰 시점 공급 페이즈 (2026-08-16): 클라가 몸집·문양으로 "세졌다"를 읽는 근거.
        public int PhaseTier { get; set; }

        // 추격 주인 (2026-08-16 유저 결정: 플레이어별로 추격 몹이 각자 할당되고 그 몹만 쫓는다).
        // 스폰 시 배정 구역의 참가자 중 담당이 가장 적은 사람에게 붙는다 — 구역 목표가 이미
        // 인당(PerPlayerTarget × 인원)이므로, 주인을 나눠야 그 몫이 실제로 각자에게 간다.
        // 0이면 주인 없음(캠프·보스 등 구형 경로) — 그때는 종전대로 최근접을 쫓는다.
        // 주인이 구역을 떠나거나 탈락하면 0으로 풀려 같은 구역의 다른 사람에게 재배정된다.
        public long OwnerPlayerId { get; set; }

        // 착탄 예약 (#229 과잉 사격 방지): 발사 시점에 물려 둔 미착탄 피해 합.
        // 오브는 착탄이 지연되므로, 예약을 안 세면 전 오브가 같은 몹에 몰려 쏘고 그중
        // 한 발만 유효하다 — 오브를 늘려도 한 사격에 한 마리씩만 죽던 원인이다.
        public int PendingDamage { get; set; }
        public bool Infiltrating { get; set; }

        // 추격 행군인가 (2026-08-16): 초기 침투는 못 뚫으면 걷어내지만, 이미 나를 쫓던
        // 몹의 추격은 제자리에서 멈출 뿐 사라지지 않는다.
        public bool MarchIsPursuit { get; set; }
        public List<Vector3f> MarchWaypoints { get; } = new();
        public int MarchIndex { get; set; }
        public double MarchBudgetSeconds { get; set; }

        // 행군 레인 (#232 원형 산개): 같은 경로를 걷는 몹이 한 줄로 겹치지 않게, 웨이포인트를
        // 진행 방향의 수직으로 이만큼 비켜 걷고 속도도 조금씩 다르다. 스폰 시 한 번 정한다.
        public float MarchLaneOffset { get; set; }
        public float MarchSpeedScale { get; set; } = 1f;

        public DateTime NextChasePlanAtUtc { get; set; }

        // 정지 감시용
        public float StuckWatchX { get; set; }
        public float StuckWatchY { get; set; }
        public DateTime StuckSinceUtc { get; set; }
        public bool StuckReported { get; set; }

        /// <summary>캠프 몹은 앵커에 묶이고, 공급 몹은 구역 자체가 리쉬다.</summary>
        public bool IsSupplyUnit => CampIndex < 0;

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
            Phase = PhaseTier,
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

    /// <summary>정지 감시 보고 (임시 진단) — GameServer가 로그로 옮겨 적는다.</summary>
    public List<string> StuckReports { get; } = new();
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
    SwarmMonsterKind Kind = SwarmMonsterKind.Skeleton,
    // 기준점 계측 (#232 1단계): 처치 시점의 생존초와 살아 있는 동안 받은 공격 사건 수.
    double AliveSeconds = 0d,
    int AttackEventCount = 0)
{
    public static SwarmArenaDamageResult None => new(false, false, 0, null);
}

public readonly record struct SwarmArenaCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    int MonsterId);

public readonly record struct SwarmMonsterSummary(
    int HitsTaken,
    int Kills,
    IReadOnlyDictionary<string, int> PatternHits)
{
    public static SwarmMonsterSummary Empty => new(0, 0, new Dictionary<string, int>());
}
