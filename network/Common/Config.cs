// ReSharper disable All
using System;

namespace network.common
{
    /// <summary>
    /// 네트워크 및 서버 관련 설정 상수
    /// </summary>
    public class Config
    {
        private const int BroadcastChunkSize = 200;

        // Network Settings
        /// <summary>최대 동시 연결 수</summary>
        public static readonly int MAX_CONNECTION = 1000;

        /// <summary>소켓 당 미리 할당할 버퍼 개수</summary>
        public static readonly int PRE_ALLOC_COUNT = 2;

        /// <summary>패킷 버퍼 크기 (바이트)</summary>
        public static readonly int BUFFER_SIZE = 2048;

        /// <summary>패킷 헤더 크기 (바이트) - 길이 정보</summary>
        public static readonly int HEADER_SIZE = 4;

        /// <summary>리스닝 소켓 백로그 큐 크기</summary>
        public static readonly int BACK_LOG = 100;

        // Batch Processing Settings
        /// <summary>Redis 배치 작업 크기</summary>
        public static readonly int BATCH_SIZE = 10;

        /// <summary>브로드캐스트 단위 (패킷당 최대 오브젝트 수)</summary>
        public static readonly int BROADCAST_UNIT = BUFFER_SIZE / BroadcastChunkSize;

        // Game Logic Settings
        /// <summary>이동 큐 최대 크기</summary>
        public static readonly int MAX_MOVE_QUEUE_SIZE = 10;

        /// <summary>채팅 메시지 최대 길이</summary>
        public static readonly int MAX_CHAT_LENGTH = 30;

        // Lock Settings
        /// <summary>분산 락 TTL</summary>
        public static readonly TimeSpan LOCK_TTL = TimeSpan.FromSeconds(30);

        // Corruption 임계값
        /// <summary>불안 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_UNEASE = 25;

        /// <summary>혼란 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_CONFUSION = 50;

        /// <summary>광기 단계 시작 (정신 오염도 %)</summary>
        public const int CORRUPTION_MADNESS = 75;

        // Game Session Settings
        /// <summary>게임 세션 지속 시간 (분)</summary>
        public static readonly int GAME_DURATION_MINUTES = 15;

        /// <summary>게임 세션 지속 시간 (초)</summary>
        public static readonly int GAME_DURATION_SECONDS = GAME_DURATION_MINUTES * 60;

        /// <summary>Round/settlement loop toggle. Disabled for the current continuous-session prototype.</summary>
        public static readonly bool ROUND_SYSTEM_ENABLED = false;

        /// <summary>Round system: total round count (#168).</summary>
        public const int ROUND_TOTAL_COUNT = 4;

        /// <summary>Round system: action phase duration in seconds (#168).</summary>
        public const int ROUND_ACTION_SECONDS = 3 * 60;

        /// <summary>Round system: settlement phase duration in seconds (#168).</summary>
        public const int ROUND_SETTLEMENT_SECONDS = 45;
        public const int ROUND_SETTLEMENT_NOMINATION_SECONDS = 10;
        public const int ROUND_SETTLEMENT_RESULT_SECONDS = 3;
        public const int ROUND_SETTLEMENT_CONTRIBUTION_SECONDS = 3;
        public const int ROUND_SETTLEMENT_DETECTION_RESULT_SECONDS = 3;
        public const int ROUND_SETTLEMENT_ELIMINATION_SECONDS = 3;

        /// <summary>흔적 배치 스태미나 비용. GDD §3.1.2, 패키지 Y 2A: -10 → -5.</summary>
        public const int TRACE_PLACE_STAMINA_COST = 5;

        /// <summary>사보타주 4B: ▓▓ 위치 공개 지속 시간 (초). GDD §2.5.4.</summary>
        public const int SABOTAGE_TARGET_EXPOSE_SECONDS = 5;

        /// <summary>폐쇄 구역 체류 시 5초당 오염도 증가량. GDD §2.1.5, v0.1.9, #66.
        ///     스태미나 패널티(-20/5초)에서 오염도 패널티로 변경.
        ///     메타포: 밀폐된 위험 구역 체류 = 정신적 압박 상승.
        ///     사건·전투 등 다른 오염도 변화와 합산된다.
        ///     수치는 플레이테스트 후 최종 확정 예정 (미확정 #67). v0.1.10에서 2 → 4 → 5로 상향.</summary>
        public const int CLOSED_AREA_CORRUPTION_TICK = 5;

        /// <summary>근접 자동전투와 타겟 근접 체크리스트의 공통 거리 기준.</summary>
        public const float TARGET_PROXIMITY_DISTANCE = 3f;

        /// <summary>Survivor Royale inventory slot capacity shared by clients, bots, and the game server.</summary>
        public const int SURVIVOR_INVENTORY_SLOT_COUNT = 6;

        /// <summary>World pickup used to represent one summon stone.</summary>
        public const int SUMMON_STONE_GROUND_ITEM_ID = 107000050;

        /// <summary>
        ///     잼 지상 픽업 (#222 M3): 소환석(개봉 재화)과 분리된 승점 재화 — SB의 코인/잼
        ///     이원 구조. 큰 몹·오브 파괴가 떨구고, 잼 최다가 승리를 가른다 (M3 승리 판정).
        /// </summary>
        public const int JAM_GROUND_ITEM_ID = 107000060;

        /// <summary>하트 (#222 M4): 즉시 회복 픽업 — 고위험 몹(탈주·볼러) 처치가 유일 공급처.</summary>
        public const int HEART_GROUND_ITEM_ID = 107000070;

        /// <summary>부츠 (#222 M4): 10초 이속 버프 픽업 — 다트 고블린 드랍. 사람 전용.</summary>
        public const int BOOTS_GROUND_ITEM_ID = 107000080;
        public const int BOOTS_SPEED_DURATION_SECONDS = 10;
        // 1.5 (#222 3차): 1.4는 밋밋, 1.6은 과속 — 기본 5 → 7.5, 서버 검증 상한(10) 안.
        public const float BOOTS_MOVE_SPEED_MULTIPLIER = 1.5f;

        /// <summary>
        ///     빈손 이속 (#223, SB 정합: 스쿼드를 잃으면 빨라진다): 오브 0개 동안의 이동 배율.
        ///     부츠 중첩 시 5 × 1.3 × 1.5 = 9.75 — 서버 검증 상한(10) 안.
        /// </summary>
        public const float SWARM_BARE_MOVE_SPEED_MULTIPLIER = 1.3f;

        /// <summary>
        ///     빈손 이속 유지 시간 (#229 12단계). 빈손인 내내 빠르면 "패배 직전"이 아니라
        ///     도주 특화 상태가 된다 — 마지막 오브를 잃은 직후 이 시간만 가속하고 원복한다.
        ///     그 뒤의 빈손은 잔상의 우선 표적이 되어 재건에 쫓긴다.
        /// </summary>
        public const float SWARM_BARE_MOVE_SPEED_SECONDS = 2f;

        /// <summary>열쇠 (#222 M4): 무료 소환 1회 충전 — 탈주 고블린(미니보스) 드랍. 사람 전용.</summary>
        public const int KEY_GROUND_ITEM_ID = 107000090;

        // 상태 효과 표시 ID (status_effect_info.csv와 동기)
        public const int BOOTS_STATUS_EFFECT_ID = 1101;
        public const int KEY_STATUS_EFFECT_ID = 1102;

        /// <summary>
        ///     무방비 (2026-08-16 유저 결정, 구 "필사의 탈주"): 오브 0개 상태의 시각화.
        ///     새 능력이 아니라 이미 있는 현상을 읽히게 한 것이다 — 공격·절단 불가에
        ///     잔상 우선 표적까지 걸린 상태이므로, 이름과 설명을 그 규칙으로 갈아 끼운다.
        ///     이속 가속은 2초만 유지되므로(SWARM_BARE_MOVE_SPEED_SECONDS) 더는
        ///     "탈주 버프"가 아니다.
        /// </summary>
        public const int BARE_STATUS_EFFECT_ID = 1103;

        /// <summary>
        ///     필사의 저항 (2026-08-16 유저 결정): 절단당한 직후 반격 보호 창의 시각화.
        ///     내 꼬리를 자른 상대의 본체 공격만 무효가 된다(#227 7단계) — 제3자·잔상은
        ///     그대로 들어온다. 서버가 잔광 VFX와 같은 시점·지속으로 보낸다.
        /// </summary>
        public const int RETALIATION_STATUS_EFFECT_ID = 1104;

        /// <summary>
        ///     보스 사거리 (#223): 파도 T3 오브급(기본 2.5 + 가중치 4 × 0.4) — 제자리 고정
        ///     포대의 위협 반경. 서버 판정과 클라 범위 링이 이 값을 공유한다 (표시 = 판정).
        /// </summary>
        public const float SWARM_BOSS_ATTACK_RANGE = 4.1f;

        /// <summary>
        ///     3머지 비활성 (#226 오브열): 열 문법에서 성장 = 길이 — 같은 색 3개 압축(3→1)은
        ///     그 언어와 싸운다. 소환마다 열이 길어지고, 티어는 상자 시간 등급이 공급한다.
        /// </summary>
        public static readonly bool SWARM_ORB_MERGE_ENABLED = false;

        // 오브열 (#226 실험 α/β): 오브가 이동 경로를 따라오는 전투열 — 클라 배치와
        // 서버 판정(오브별 공격 원점·본체 접촉)이 같은 값을 쓴다 (표시 = 판정).
        // 0.9→0.7 (#227): 꼬리를 촘촘하게 — 열 응집감 + 림 메타볼 연결 강화.
        public const float SWARM_ORB_TRAIL_SPACING = 0.7f;
        public const float SWARM_ORB_TRAIL_FIRST_OFFSET = 0.7f;

        /// <summary>본체(머리)-상대 오브열 접촉 반경 — P0-a 접촉 판정(0.45)보다 오브 몸집만큼 여유.</summary>
        public const float SWARM_ORB_TRAIL_CONTACT_RADIUS = 0.6f;

        /// <summary>열 접촉 오염 (slither 비대칭 번역): 머리는 항상 취약 — 오브 HP를 우회해 본체 직행.</summary>
        public const int SWARM_ORB_TRAIL_CONTACT_CORRUPTION = 35;

        /// <summary>Survivor Royale combat and closure elimination threshold.</summary>
        public const int SURVIVOR_MAX_CORRUPTION = 420;

        /// <summary>근접 자동전투 P0. 활성화 중에는 기존 수동 분필 공격 진입을 숨긴다.</summary>
        public static readonly bool PROXIMITY_AUTO_COMBAT_P0_ENABLED = true;

        /// <summary>
        /// Survivor Royale #202 uses monster rewards as summon currency instead of direct orb exploration loot.
        /// Legacy area pools stay loadable for data validation and isolated regression tests.
        /// </summary>
        public static readonly bool MONSTER_SUMMON_ECONOMY_ENABLED = true;

        /// <summary>
        /// Issue #216 vertical slice: four linked home spots, marching waves, and respawning players.
        /// This branch intentionally bypasses the orb economy and the #214 room phase machine.
        /// </summary>
        public static readonly bool SPOT_ARENA_P0_ENABLED = true;

        /// <summary>
        ///     Issue #217 스웜 회피 P0-a: 사람 1명 + 패턴 스폰 잔상 스웜. 잔상은 공급이 아니라
        ///     회피해야 하는 압력이다. 켜지면 스팟 아레나 대신 스웜 아레나가 매치를 소유한다.
        ///     SPOT_ARENA_P0_ENABLED는 레거시 시스템(폐쇄·오브 경제·페이즈)을 끄는 게이트로 유지한다.
        /// </summary>
        public static readonly bool SWARM_P0_ENABLED = true;

        /// <summary>
        ///     #229 5단계: 스웜에서 탐색(상자)과 소비품(하트·부츠)을 임시로 끈다.
        ///     회복은 수면이, 기동력은 바람 오브가 맡는다 — 랜덤 상자가 그 자리를 대신하면
        ///     빌드로 읽혀야 할 것이 운으로 읽힌다. 데이터·CSV·프리팹·레거시 코드는 남긴다:
        ///     이 플래그만 되돌리면 다른 모드와 함께 그대로 살아난다.
        /// </summary>
        public static readonly bool SWARM_EXPLORE_AND_CONSUMABLES_ENABLED = false;

        /// <summary>스웜에서 탐색·소비품이 꺼졌는지 — 호출부가 매번 두 플래그를 조합하지 않게 한다.</summary>
        public static bool IsSwarmExploreDisabled() =>
            SWARM_P0_ENABLED && !SWARM_EXPLORE_AND_CONSUMABLES_ENABLED;

        /// <summary>
        ///     스웜 아레나 매치 정원. P0-a는 1(솔로), P0-b는 2, 3쌍 깔때기(성장곡선 v3)는 6.
        ///     사람은 항상 1명이고 나머지는 봇으로 채운다.
        /// </summary>
        // #223 10인 전환 (2026-08-11, M5): SB 정원 10 — 포드 10곳 전원 유니크 스폰
        // (도서관·체육관도 스폰 풀에 편입).
        public static readonly int SWARM_PLAYERS_PER_MATCH = 10;

        /// <summary>
        ///     매치 길이 (#226 단계 B) — 5분 오브 점수전. 개전(카운트다운 종료) 앵커 기준이며,
        ///     만료 시 생존자 중 오브 최다 보유자가 승리한다(동점: 티어 합 → 철갑 → 본체 게이지).
        ///     클라 타이머(GameStatusDisplay)와 폐쇄 시간표(AreaClosureManager 최종 웨이브)가
        ///     같은 값에 정렬된다. 잼 승점·4분 잼 타임아웃(#222 M3-2)은 퇴역.
        /// </summary>
        public const int SWARM_MATCH_DURATION_SECONDS = 300;

        /// <summary>
        ///     스웜 탐색 스팟 개봉 비용은 SB 상자 문법을 따른다: 스쿼드(궤도 오브)가 클수록
        ///     다음 개봉이 비싸진다. 3머지가 오브 수를 줄이면 비용이 도로 내려간다 —
        ///     슬롯 차단 대신 비용 곡선이 성장을 억제한다. 사람·봇 공통.
        /// </summary>
        // 5 → 1 (#226 웨이브 전환): 상시 쫓기는 판에서 첫 소환이 5석이면 초반이 마른다 —
        // 초반은 싸게, 성장 억제는 오브 수 비례 가산이 맡는다.
        public const int SWARM_EXPLORE_COST_BASE = 1;

        /// <summary>스팟 리젠 시간(초). 개봉된 스팟은 사라지지 않고 이 시간 뒤 다시 나온다.
        ///     0 → 30 (#226 단계 C): 상자 = 소모품(하트·부츠) 공급처 — 즉시 리젠이면 하트가 무한이다.</summary>
        public const int SWARM_EXPLORE_REGEN_SECONDS = 30;

        /// <summary>
        ///     상자 개봉 비용 (#226 단계 C): 상자는 오브가 아니라 소모품(하트·부츠)을 준다 —
        ///     소환석의 주 소비처는 성장 카드이므로 상자는 고정 저가.
        /// </summary>
        public const int SWARM_BOX_OPEN_COST = 1;

        /// <summary>
        ///     성장 카드 기본 비용 (#229): 이번 판 성공한 성장 선택 횟수 N 기반 5+2N.
        ///     오브가 잘려도 N은 줄지 않아 절단이 성장 시간을 초기화하지 못한다.
        /// </summary>
        public static int GetSwarmGrowthBaseCost(int growthSuccessCount) =>
            5 + 2 * Math.Max(0, growthSuccessCount);

        /// <summary>#229에서는 보유 오브 수 할증을 쓰지 않는다. 로그 호환을 위해 0을 남긴다.</summary>
        public static int GetSwarmGrowthScoreSurcharge(int orbCount) => 0;

        /// <summary>5분 매치에서 후반 성장을 제한하는 성장 카드 상한 비용 (#229).</summary>
        public const int SWARM_GROWTH_COST_CAP = 21;

        /// <summary>
        ///     성장 카드 최종 비용 = min(21, 5+2N). 0오브는 비용 3의 T1 생성 보장(재건 경로).
        ///     보유 오브 수는 가격에 영향을 주지 않는다.
        /// </summary>
        public static int GetSwarmGrowthCardCost(int growthSuccessCount, int orbCount) =>
            orbCount <= 0
                ? 3
                : Math.Min(SWARM_GROWTH_COST_CAP, GetSwarmGrowthBaseCost(growthSuccessCount));

        /// <summary>
        ///     시작 지급 소환석 (2026-08-16 유저 결정). 오브를 들려 주는 대신, 오브 3개를 살 수
        ///     있는 만큼의 소환석으로 시작한다 — 첫 성장을 플레이어가 직접 고르게 해서 판이
        ///     선택으로 열리고, 소환·공격강화·방어강화 중 무엇을 먼저 세울지가 갈린다.
        ///     같은 곡선으로 계산하므로 비용 곡선을 바꾸면 지급량이 따라온다.
        ///     현재 값 = 3(0오브 보장가) + 7(N=1) + 9(N=2) = 19.
        /// </summary>
        public static int GetSwarmStartingStoneGrant()
        {
            int total = 0;
            for (int purchased = 0; purchased < SWARM_STARTING_ORB_COUNT; purchased++)
                total += GetSwarmGrowthCardCost(purchased, purchased);
            return total;
        }

        /// <summary>궤도 오브 1개당 개봉 비용 가산 — SB "스쿼드 인원수 비례 상자 코인".</summary>
        public const int SWARM_EXPLORE_COST_PER_ORB = 2;

        /// <summary>
        ///     기본가 허용량 — 오브가 이 수 이하면 기본가(5)에서 출발하고, 성장분에만 가산이 붙는다.
        ///     (#219 M2: 시작 오브 지급은 퇴역 — 이 값은 가격 곡선의 피벗으로만 남는다)
        /// </summary>
        public const int SWARM_STARTING_ORB_COUNT = 3;

        /// <summary>
        ///     빈손(오브 0개)은 개봉 무료 — 빈손 시작의 첫 오브와 전멸 후 재기가 같은 경로로 성립한다.
        /// </summary>
        public static int GetSwarmExploreCost(int orbCount) =>
            orbCount <= 0
                ? 0
                : SWARM_EXPLORE_COST_BASE +
                  SWARM_EXPLORE_COST_PER_ORB * Math.Max(0, orbCount - SWARM_STARTING_ORB_COUNT);

        /// <summary>
        ///     예산 초과 스팟의 선소진용 — 판보다 긴 쿨다운으로 영구 봉인을 표현한다.
        ///     (일반 개봉은 SWARM_EXPLORE_REGEN_SECONDS 리젠으로 되돌아온다.)
        /// </summary>
        public const int SWARM_EXPLORE_CONSUME_SECONDS = 100_000;

        /// <summary>
        ///     궤도 스쿼드(#217 오브 성장 개편): 6칸 보드를 폐지하고 궤도 오브 수가 곧 성장이다.
        ///     같은 색 3개가 모이면 자동으로 상위 티어(같은 색)로 합쳐진다 — SB 3머지 문법.
        ///     SB에는 슬롯 하드캡이 없다 — 오브 수 비례 개봉 비용이 성장을 억제하고, 이 값은
        ///     이상 상황 방지용 안전상한일 뿐이다. 클라 궤도 슬롯 수와 같아야 한다 (PlayerTool.MaxOrbSlots).
        /// </summary>
        // 30 → 99 (#226 오브열): 머지 폐지로 성장 = 열 길이 — 사실상 무제한, 비용 곡선이 억제자.
        // 99 → 6 (#232 1절): 꼬리는 6칸 빌드판이다. 성장은 길이가 아니라 유지·합성·교체로 돈다.
        // 매치 2749 실측 최대 43개에서는 상대가 어떤 오브를 왜 들고 있는지 읽히지 않았다.
        // 클라 PlayerTool.MaxOrbSlots(99)는 배열 크기 상한이라 그대로 두어도 6개만 채워진다.
        public const int SWARM_ORB_CAPACITY = 6;

        /// <summary>현재 모드의 오브 보유 상한 — 스웜(궤도 스쿼드)은 9, 레거시 보드는 6.</summary>
        public static int GetOrbCapacity() =>
            SWARM_P0_ENABLED ? SWARM_ORB_CAPACITY : SURVIVOR_INVENTORY_SLOT_COUNT;

        /// <summary>
        ///     스웜 기본 오브 사거리. 서버 전투(GameServer.SwarmArena)와 클라 사거리 링
        ///     (PlayerRangeRing)이 같은 값을 읽어야 표시와 판정이 일치한다.
        ///     7 → … → 3 → 2.5 (2026-08-07): 좁은 시작이 파도(사거리 성장) 여지다.
        ///     다트 고블린 사거리(5)의 절반 — 원거리 몹 접근엔 피격 감수가 전제.
        /// </summary>
        public const float SWARM_ORB_ATTACK_RANGE = 2.5f;

        /// <summary>
        ///     유저간 사격 사거리 (2026-08-16 유저 명세). PvE(7)보다 짧게 — 붙어야 싸운다.
        ///     플레이어 본체 기준으로 잰다: 오브별 원점으로 재면 꼬리가 길수록 사정권이
        ///     늘어나 "오브 수는 PvP 화력을 키우지 않는다"는 규칙과 어긋나고, 링 하나로
        ///     표시할 수도 없다. 클라 표시(PlayerRangeRing)가 같은 값을 읽는다.
        /// </summary>
        public const float SWARM_PVP_ATTACK_RANGE = 5f;

        /// <summary>
        ///     유저간 사격에 참여하는 오브 수 = 앞열 이만큼 (2026-08-16 유저 명세).
        ///     전체 오브가 사람을 쏘면 20개 꼬리가 3개 꼬리를 그대로 녹인다. 상한을 두면
        ///     오브 수는 PvE 성장과 절단 위험만 키우는 축이 된다.
        /// </summary>
        public const int SWARM_PVP_ORB_COUNT = 3;

        // ===== 교차사격 (#232 2단계) =====
        // 오브는 몬스터만 쏜다. 그 공격이 만드는 모양(태양 = 직선)에 다른 플레이어가 들어오면
        // 고정 충격을 받는다. 티어는 모양의 크기만 키우고 충격값은 안 키운다.
        // 서버 판정과 클라 예고 표시가 같은 값을 읽어야 "표시 = 판정"이 성립한다.

        /// <summary>교차사격 충격 1회의 정신오염. 티어·공격 강화와 무관한 고정값.</summary>
        public const int SWARM_CROSSFIRE_SHOCK_CORRUPTION = 50;

        /// <summary>같은 피해자는 공격자와 무관하게 이 시간 동안 추가 충격을 받지 않는다.</summary>
        public const float SWARM_CROSSFIRE_VICTIM_IMMUNE_SECONDS = 0.9f;

        /// <summary>한 공격자가 다른 플레이어에게 만드는 유효 충격 상한 — 초당 1회.</summary>
        public const float SWARM_CROSSFIRE_OWNER_HIT_INTERVAL_SECONDS = 1f;

        /// <summary>
        ///     한 플레이어가 동시에 유지할 수 있는 교차사격 예고 수. 태양 오브 셋이 각자 한 줄씩 —
        ///     넘치는 발은 모양 없이 기준 몬스터만 때린다(화력 보존, 화면 포화 방지).
        /// </summary>
        public const int SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER = 3;

        /// <summary>
        ///     태양 직선 (2026-08-17 유저 판정: 미사일이 아니라 "경고색이 깜빡인 뒤 큰 공격이 한 번에
        ///     천천히 지나간다"). 예고 시간 동안 깜빡이고, 그 뒤 판정 앞머리가 원점에서 끝까지 이 속도로
        ///     쓸고 지나간다. 지나간 자리의 몬스터는 PvE 피해(관통), 플레이어는 충격 1회.
        /// </summary>
        public const float SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS = 0.55f;
        public const float SWARM_CROSSFIRE_SUN_SWEEP_SPEED = 4.5f;

        /// <summary>
        ///     큰 공격 한 번 = 유도탄 두 발 몫. 주기 ×2, 피해 ×2 — 총 화력은 같고 한 번의 무게가 커진다.
        ///     T1 24는 일반 몹(16~22)을 한 방에 지우고 관통하므로 실측 뒤 조정 대상이다.
        /// </summary>
        public const float SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER = 2f;
        public const float SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER = 2f;

        /// <summary>태양 직선의 전체 폭(T1/T2/T3)과 기준 몬스터 너머 연장 길이(T1/T2/T3). 폭은 바닥면 단위.</summary>
        public static readonly float[] SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER = { 0.7f, 0.85f, 1f };
        public static readonly float[] SWARM_CROSSFIRE_SUN_EXTEND_BY_TIER = { 2.5f, 3f, 3.5f };

        /// <summary>교차사격 모양 종류 — 패킷·로그·클라 렌더가 공유하는 식별자.</summary>
        public const int SWARM_CROSSFIRE_SHAPE_LINE = 1;

        /// <summary>
        /// Survivor Royale P0에서는 레거시 마니또 체크리스트를 생성하거나 진행하지 않는다.
        /// 데이터와 프로토콜은 보존하므로 레거시 모드가 다시 분리되면 이 게이트로 복구할 수 있다.
        /// </summary>
        public static readonly bool CHECKLIST_SYSTEM_ENABLED = false;
    }
}
