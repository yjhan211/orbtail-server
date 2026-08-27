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

        /// <summary>폐쇄 구역 체류 시 5초당 오염도 증가량. GDD §2.1.5, v0.1.9, #66.
        ///     스태미나 패널티(-20/5초)에서 오염도 패널티로 변경.
        ///     메타포: 밀폐된 위험 구역 체류 = 정신적 압박 상승.
        ///     사건·전투 등 다른 오염도 변화와 합산된다.
        ///     수치는 플레이테스트 후 최종 확정 예정 (미확정 #67). v0.1.10에서 2 → 4 → 5로 상향.</summary>
        public const int CLOSED_AREA_CORRUPTION_TICK = 5;

        /// <summary>근접 자동전투와 타겟 근접 체크리스트의 공통 거리 기준.</summary>
        public const float TARGET_PROXIMITY_DISTANCE = 3f;

        /// <summary>Survivor Royale inventory slot capacity shared by clients, bots, and the game server.</summary>
        public const int LEGACY_INVENTORY_SLOT_COUNT = 6;

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

        /// <summary>침수 (#268, 2026-08-25): 파도 소용돌이 피격 — 5초 이동 감속 디버프.</summary>
        public const int WAVE_SOAKED_STATUS_EFFECT_ID = 1105;

        /// <summary>화상 (#268, 2026-08-25): 태양 미사일 피격 — 3초 틱 피해 디버프.</summary>
        public const int SUN_BURN_STATUS_EFFECT_ID = 1106;

        /// <summary>상처 (#268, 2026-08-25): 바람 칼날 피격 — 5초간 치명타 피격 확률 증가 디버프.</summary>
        public const int WIND_WOUND_STATUS_EFFECT_ID = 1107;

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

        /// <summary>파도(Blue) 오브 공급 — 단색 검증이 필요하면 false로 차단한다.</summary>
        public static readonly bool SWARM_WAVE_ORB_ENABLED = true;

        /// <summary>태양(Red) 오브 공급 — 단색 검증이 필요하면 false로 차단한다. (2026-08-27 파도 단색 — 유저 지시)</summary>
        public static readonly bool SWARM_SUN_ORB_ENABLED = false;

        /// <summary>
        ///     바람(Green) 오브 공급 — 단색 검증이 필요하면 false로 차단한다.
        ///     세 토글을 전부 끄면 공급 풀이 비므로 최소 하나는 켜 둘 것. (2026-08-27 파도 단색 — 유저 지시)
        /// </summary>
        public static readonly bool SWARM_WIND_ORB_ENABLED = false;

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
        public const int MAX_CORRUPTION = 420;

        /// <summary>
        /// Survivor Royale #202 uses monster rewards as summon currency instead of direct orb exploration loot.
        /// Legacy area pools stay loadable for data validation and isolated regression tests.
        /// </summary>
        public static readonly bool MONSTER_SUMMON_ECONOMY_ENABLED = true;

        /// <summary>
        ///     #229 5단계: 스웜에서 탐색(상자)과 소비품(하트·부츠)을 임시로 끈다.
        ///     회복은 수면이, 기동력은 바람 오브가 맡는다 — 랜덤 상자가 그 자리를 대신하면
        ///     빌드로 읽혀야 할 것이 운으로 읽힌다. 데이터·CSV·프리팹·레거시 코드는 남긴다:
        ///     이 플래그만 되돌리면 다른 모드와 함께 그대로 살아난다.
        /// </summary>
        public static readonly bool SWARM_EXPLORE_AND_CONSUMABLES_ENABLED = false;

        /// <summary>스웜에서 탐색·소비품이 꺼졌는지 — 호출부가 매번 두 플래그를 조합하지 않게 한다.</summary>
        public static bool IsSwarmExploreDisabled() => !SWARM_EXPLORE_AND_CONSUMABLES_ENABLED;

        /// <summary>
        ///     스웜 아레나 매치 정원. P0-a는 1(솔로), P0-b는 2, 3쌍 깔때기(성장곡선 v3)는 6.
        ///     사람은 항상 1명이고 나머지는 봇으로 채운다.
        /// </summary>
        // #223 10인 전환 (2026-08-11, M5) → #272 8인 전환 (2026-08-27): School2 신맵은
        // 1인 시작방 8곳 × 합류 4세트 동심원 구조 — 정원 = 시작방 수.
        public static readonly int SWARM_PLAYERS_PER_MATCH = 8;

        /// <summary>
        ///     #272 매치 맵 단일 원천 — 스웜 매치가 도는 맵. 매치 경로의 모든 맵 참조는
        ///     MapId.School 하드코딩 대신 이 상수를 본다 (School은 데이터·테스트로 보존).
        /// </summary>
        public static readonly MapId SWARM_MATCH_MAP = MapId.School2;

        /// <summary>
        ///     매치 맵의 중앙 수렴 구역 (자기장 중심·보스 무대·교차사격 샌드박스 스폰).
        ///     #272 가운데 병합 (2026-08-27 유저 지시): 1차 통로·테라스·운동장을 S2Corridor9
        ///     하나로 묶었다 — 구역 단위 프랍 가시성이 광장 내부에서 토글되지 않게. 자기장
        ///     중심은 이 구역 rect들의 경계 상자 중심(138.5, 23)이라 병합 전과 동일하다.
        /// </summary>
        public static readonly AreaType SWARM_MATCH_GROUND_AREA = AreaType.S2Corridor9;

        /// <summary>
        ///     매치 길이 (#226 단계 B) — 5분 오브 점수전. 개전(카운트다운 종료) 앵커 기준이며,
        ///     만료 시 생존자 중 오브 최다 보유자가 승리한다(동점: 티어 합 → 철갑 → 본체 게이지).
        ///     클라 타이머(GameStatusDisplay)와 폐쇄 시간표(AreaClosureManager 최종 웨이브)가
        ///     같은 값에 정렬된다. 잼 승점·4분 잼 타임아웃(#222 M3-2)은 퇴역.
        /// </summary>
        public const int SWARM_MATCH_DURATION_SECONDS = 300;

        /// <summary>
        ///     #272 자기장 폐쇄 — 운동장 중심 원형 수축 필드(SwarmPressureField)가 폐쇄 시간표의
        ///     단일 원천. 구역 폐쇄 이벤트(경고·문 잠금·꼬리 파괴)는 필드에서 파생한 구역별
        ///     완전-밖 시각을 쓰고, 오염은 경계 초과 거리 비례가 전담한다. false 롤백은 School
        ///     세대 고정 웨이브(DefaultP0Waves)로의 복귀라 School2 매치 맵에서는 무의미하다(#272) —
        ///     자기장이 유일 시간표다. 끄면 클라 경계 렌더·자기장 오염이 함께 꺼진다.
        /// </summary>
        public static readonly bool SWARM_PRESSURE_FIELD_ENABLED = true;

        /// <summary>
        ///     자기장 수축 유예(초) — 개전 후 이 시간 동안 전 맵 안전, 이후 매치 종료까지 안전
        ///     반경이 최대치에서 0으로 선형 수축한다 (종료 시 운동장 중심만 안전). 서버 판정과
        ///     클라 경계 렌더가 같은 값으로 보간한다.
        ///     60 → 0 (2026-08-26 유저 결정): 개전 즉시 매치 전체 길이에 걸쳐 천천히 조인다 —
        ///     경계가 처음부터 존재해야 경계 토출 몹 스폰의 원천이 마르지 않는다.
        /// </summary>
        public const int SWARM_FIELD_HOLD_SECONDS = 0;

        /// <summary>
        ///     자기장 수축 곡선 지수 (#272, 2026-08-27 유저 결정 "방이 짧고 운동장이 길다"):
        ///     1 = 선형, 커질수록 초반 느리고 후반 빠르다 (안전 반경 = Max×(1−진행률^지수)).
        ///     1.4 기준 시작방 폐쇄 87→약 124초, 합류 141→약 175초, 종반 압축. 서버 판정·클라
        ///     경계 렌더·파생 시간표가 SwarmPressureField의 같은 곡선 함수를 쓴다.
        /// </summary>
        public const double SWARM_FIELD_SHRINK_EXPONENT = 1.4d;

        /// <summary>문 게이지 시간(초) — 시작방 문: 혼자 여는 관문이라 짧다. 봇 채널도 같은 값.</summary>
        public const float SWARM_DOOR_GAUGE_SECONDS = 3f;

        /// <summary>
        ///     합류→중앙 문 게이지(초) (#272, 2026-08-27 유저 결정 "J에서 둘이 싸우게"):
        ///     길게 잡아 선착자도 후착자 도착 전까지 못 나가고, 두 번째 문부터는 피격이 게이지를
        ///     리셋하므로 "문을 열려면 상대를 먼저 처리해야 한다"가 규칙에서 나온다.
        /// </summary>
        public const float SWARM_JOIN_DOOR_GAUGE_SECONDS = 12f;

        /// <summary>#272 School2 문 등급: 합류 문(211~222)은 듀얼 관문 게이지를 쓴다.</summary>
        public static float GetSwarmDoorGaugeSeconds(int doorId)
        {
            return doorId is >= 211 and <= 222
                ? SWARM_JOIN_DOOR_GAUGE_SECONDS
                : SWARM_DOOR_GAUGE_SECONDS;
        }

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
        // 6 → 99 (#232 무한 꼬리, 2026-08-17): 플레이어에게 보이는 상한은 없다. 이 값은 정상 5분
        // 매치에서 닿지 않는 내부 안전장치일 뿐이고, 포화 상태·포화 해소 UI는 쓰지 않는다.
        // 클라 PlayerTool.MaxOrbSlots(99)와 같아야 한다.
        public const int SWARM_ORB_CAPACITY = 99;

        /// <summary>현재 모드의 오브 보유 상한 — 스웜(궤도 스쿼드)은 9, 레거시 보드는 6.</summary>
        public static int GetOrbCapacity() =>
            SWARM_ORB_CAPACITY;

        /// <summary>
        ///     스웜 기본 오브 사거리. 서버 전투(GameServer.SwarmArena)와 클라 사거리 링
        ///     (PlayerRangeRing)이 같은 값을 읽어야 표시와 판정이 일치한다.
        ///     7 → … → 3 → 2.5 (2026-08-07): 좁은 시작이 파도(사거리 성장) 여지다.
        ///     다트 고블린 사거리(5)의 절반 — 원거리 몹 접근엔 피격 감수가 전제.
        /// </summary>
        public const float SWARM_ORB_ATTACK_RANGE = 2.5f;

        /// <summary>
        ///     오브 궤도 (#232, 2026-08-17 서버 공유): 오브는 본체 주위 타원 궤도를 돈다 — 이동한 거리만큼
        ///     (2026-08-17 유저 지시: 이동할 때 돌고 멈추면 선다). 위상 = 시드 + 이동 거리 × 도/단위.
        ///     서버가 검증 이동으로 적산해 G_TO_C_MOVE에 실어 보내고(권위), 클라는 자기 트랜스폼 이동으로
        ///     같은 식을 적산하다 그 값으로 보정한다 — 서버는 그 자리를 오브별 발사 원점·표적 선정 기준으로
        ///     쓰고, 클라는 그 자리에 그린다. 9도/단위 = 걷기 속도 6에서 54도/초.
        ///     텔레포트(구역 이동)만큼의 점프는 적산하지 않는다.
        ///     반지름 = 사거리 × 배수, 아이소 타원(y 절반), 궤도 중심 = 본체 + Y 오프셋.
        /// </summary>
        public const float SWARM_ORB_ORBIT_DEGREES_PER_UNIT = 9f;
        public const float SWARM_ORB_ORBIT_TELEPORT_DISTANCE = 3f;
        public const float SWARM_ORB_ORBIT_RADIUS_MULTIPLIER = 0.8f;
        public const float SWARM_ORB_ORBIT_ISO_Y_SCALE = 0.5f;
        public const float SWARM_ORB_ORBIT_CENTER_OFFSET_Y = 0.8f;

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

        /// <summary>
        ///     받는 피해 배율 (2026-08-18 유저 지시 "봇·플레이어 전부 지금의 1/3만 받게"): 사람·봇 공통,
        ///     PvP 충격(태양·바람·파도)과 잔상 접촉·원거리 피해에 곱한다. 절단 자해(+35)와 폐쇄 즉사는 대상 아님.
        ///     최솟값 1 — 0이 되면 "맞았는데 안 닳는" 피격이 생긴다.
        /// </summary>
        public const float SWARM_DAMAGE_TAKEN_MULTIPLIER = 1f / 3f;

        /// <summary>받는 피해에 배율을 적용한 정수값 — 반올림, 최솟값 1.</summary>
        public static int ScaleSwarmDamageTaken(int damage) =>
            damage <= 0 ? damage : Math.Max(1, (int)Math.Round(damage * SWARM_DAMAGE_TAKEN_MULTIPLIER));

        // 충격 면역 퇴역 이력: 소유자 초당 1회 상한(2026-08-24) → 피해자 0.9초 면역
        // (SWARM_CROSSFIRE_VICTIM_IMMUNE_SECONDS)도 2026-08-26 퇴역 — 태양 다발 화망에서
        // 첫 발 이후가 소리 없이 관통해 "안 맞는" 오독을 만들었다. 지나간 발은 다 맞는다.

        /// <summary>
        ///     화상 (#268, 2026-08-25): 태양 미사일 충격에 맞으면 3초간 매초 틱 피해 —
        ///     틱당 = 충격의 0.2배(≈3). 재피격 시 지속이 갱신된다(중첩 없음). 몹은 제외 —
        ///     태양 PvE 화력은 이미 직격이 정점이다.
        /// </summary>
        public const float SWARM_SUN_BURN_SECONDS = 3f;
        public const float SWARM_SUN_BURN_TICK_INTERVAL_SECONDS = 1f;
        public const float SWARM_SUN_BURN_TICK_DAMAGE_MULTIPLIER = 0.2f;

        /// <summary>
        ///     상처 (#268, 2026-08-25): 바람 칼날 충격에 맞으면 5초간, 이후 받는 PvP 충격이
        ///     이 확률로 치명타(PvE와 같은 2배)가 된다. 평시 PvP 충격은 치명타가 없다 —
        ///     상처가 그 문을 연다. 재피격 시 지속 갱신(중첩 없음).
        /// </summary>
        public const float SWARM_WIND_WOUND_SECONDS = 5f;
        public const float SWARM_WIND_WOUND_CRIT_CHANCE = 0.35f;

        /// <summary>
        ///     한 플레이어가 동시에 유지할 수 있는 교차사격 예고 수 (명세 "동시 예고 최대 2개"). 예고(시전)
        ///     중인 모양만 센다 — 예고 시간이 0인 지금은 사실상 안 걸리고, 예고를 되살릴 때를 위해 남긴다.
        ///     상한에 닿은 소유자의 태양은 표적을 잡지 않고 기다렸다가(리졸버 필터) 자리가 나면 쏜다 —
        ///     버리지 않는다. 모양 없이 때리던 옛 폴백은 "안 맞은 몹이 죽는" 보이지 않는 피해였다
        ///     (2026-08-17 유저 제보: 봇 매치에서 예고 594건에 폴백 1293건). 표시 = 판정.
        /// </summary>
        public const int SWARM_CROSSFIRE_MAX_TELEGRAPHS_PER_OWNER = 2;

        /// <summary>
        ///     태양 투사체 (2026-08-24 최종): 같은 타일 X/Y축 표적을 향해 큰 구체 하나가 직진하며
        ///     선상의 몬스터·플레이어를 대상당 한 번 관통 타격한다. 벽에서는 피해 없는 시각 폭발,
        ///     벽 없는 끝점에서는 폭발 없이 소멸한다. 예고 시간에는 고정된 시안색 바닥 경로선이
        ///     차오르고, 발사 순간 0.22초 점멸·페이드한 뒤 비행은 꼬리 없는 태양 구체가 전달한다.
        /// </summary>
        public const float SWARM_CROSSFIRE_SUN_TELEGRAPH_SECONDS = 0.25f;
        // 서버 앞머리 속도. 클라 투사체는 패킷의 ActiveSeconds(= 실제 벽까지 거리/속도)를 그대로 써
        // 표시와 판정의 도착 시간을 맞춘다.
        public const float SWARM_CROSSFIRE_SUN_SWEEP_SPEED = 7.5f;

        /// <summary>
        ///     큰 공격 한 번 = 유도탄 두 발 몫. 주기 ×2, 피해 ×2 — 총 화력은 같고 한 번의 무게가 커진다.
        ///     T1 24는 일반 몹(16~22)을 한 방에 지우고 관통하므로 실측 뒤 조정 대상이다.
        /// </summary>
        public const float SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER = 2f;
        public const float SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER = 2f;

        /// <summary>
        ///     태양 투사체의 판정 폭(T1/T2/T3, 바닥면 단위) — 이 안에 몸이 걸리면 닿은 것.
        ///     2026-08-26 ×2 실험은 같은 날 원복 (유저 제보 "허공에서 맞는다"): "안 맞는" 체감의
        ///     원인은 폭이 아니라 세로 축이었다 — 몸통 캡슐 판정이 그걸 풀었으니 폭은 원래대로.
        /// </summary>
        public static readonly float[] SWARM_CROSSFIRE_SUN_WIDTH_BY_TIER = { 0.7f, 0.85f, 1f };

        /// <summary>
        ///     태양 표적 획득 거리(T1/T2/T3, 바닥면 단위). 투사체 길이로는 더 안 쓴다 (2026-08-24 유저
        ///     결정: 투사체는 항상 구역 경계까지 난다) — 강화는 조준이 걸리는 거리만 늘린다.
        /// </summary>
        public static readonly float[] SWARM_CROSSFIRE_SUN_RANGE_BY_TIER = { 4f, 5.5f, 7f };

        /// <summary>
        ///     바람 = 회전 칼날 (2026-08-25 유저 결정, #268): 오브가 제자리에서 돌며 반경(티어별, 바닥면) 안
        ///     전원을 주기 틱으로 간다 — 믹서기. 몬스터는 틱 PvE 피해, 소유자 아닌 플레이어는 충격(공용 면역 창).
        ///     몸통박치기(2026-08-17 결정: 감지→돌진→착지)는 퇴역 — 감지 대기가 병목이라 실효 간격 3.5초였고,
        ///     3박자 연출로도 직관적으로 읽히지 않았다. 이전에 회전 칼날을 기각했던 근거("아무도 없을 때
        ///     혼자 도는 게 이상하다")는 평시 저속 자전 → 적 감지 시 가속·발광 연출로 해소한다.
        ///     틱당 피해 = 발당 피해 × 0.75 — 슬램(× 1.5, 쿨 1.4초)과 단일 대상 DPS 동률(0.7초 틱 × 절반).
        ///     반경에 붙어야 갈리는 무기라 밀집 실효 화력 상승은 접근 리스크가 값을 치른다 (유저 판정).
        /// </summary>
        public static readonly float[] SWARM_WIND_BLADE_RADIUS_BY_TIER = { 1.4f, 1.65f, 1.9f };
        // 틱 0.35초 × 배율 0.375 (2026-08-25 2차: 0.7초 × 0.75에서 반분) — DPS는 그대로 두고
        // 타격 빈도만 두 배로. "믹서기에 갈린다"는 잘게 자주 맞아야 읽힌다 (유저 지시).
        public const float SWARM_WIND_BLADE_TICK_SECONDS = 0.35f;
        public const float SWARM_WIND_BLADE_DAMAGE_MULTIPLIER = 0.375f;
        // 시동 게이트 (2026-08-25 유저 지시 "회전 한 20퍼는 돼야 데미지"): 표적이 반경에 든
        // 순간부터 이 시간은 피해가 없다 — 클라 감지 폴링(0.15초)+가속 20% 도달(0.09초)에 맞춘
        // 값. 반경이 비면 리셋된다(클라 감속과 대칭). 옛 0.9초 게이트(체감 1.4초)와 혼동 금지.
        public const float SWARM_WIND_BLADE_SPINUP_SECONDS = 0.2f;

        /// <summary>
        ///     파도 = 소용돌이 (#268, 2026-08-25 유저 결정, 3차 "오브 위치 기준"). 물폭탄(표적
        ///     스냅샷 낙하)과 합산 소용돌이(이동 거리 게이트 + 꼬리 끝 뒤 1개)는 퇴역: 파도 오브
        ///     각각이 주기(2초)마다 자기 열 위치에 소용돌이를 깐다 — 오브가 곧 무기 위치(바람
        ///     칼날과 같은 문법). 예고(0.65초 림 링) 후 반경 안 전원(몹·플레이어 동일)을 중심으로
        ///     당기고 잠깐 늦춘다 — 피해는 타격 피드백 수준. 플레이어는 충격 면역 창(0.9초)이
        ///     연쇄 당김을 막는다.
        /// </summary>
        // 변위(당김·밀침·원 밖 축출) 실험은 전부 기각 (2026-08-25 유저 판정) — 효과는
        // "침수" 디버프(5초 25% 감속, WAVE_SOAKED_STATUS_EFFECT_ID)와 타격 피드백 피해만.
        public const float SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER = 0.25f; // 현행 물폭탄 피해의 1/4


        /// <summary>
        ///     태양 벽 충돌 시각 폭발 크기(T1/T2/T3, 바닥면 단위). 추가 피해·충격 판정은 없다.
        /// </summary>
        public static readonly float[] SWARM_CROSSFIRE_SUN_BLAST_RADIUS_BY_TIER = { 1.1f, 1.3f, 1.5f };

        /// <summary>교차사격 모양 종류 — 패킷·로그·클라 렌더가 공유하는 식별자.</summary>
        public const int SWARM_CROSSFIRE_SHAPE_LINE = 1;

        /// <summary>
        ///     교차사격 폭발 통지 — 같은 패킷(G_TO_C_SWARM_CROSSFIRE_TELEGRAPH)을 재사용한다: EventId = 터진 모양,
        ///     OriginX/Y = 폭발 지점(월드), Width = 폭발 반경(바닥면). 클라는 날아가던 투사체를 그 자리에서 터뜨린다.
        /// </summary>
        public const int SWARM_CROSSFIRE_SHAPE_DETONATE = 2;

        // ===== 6칸 빌드 (#232 4단계) =====
        /// <summary>
        ///     시작 지급 (#232 4단계, 2026-08-17): 무작위 T1 공격 오브 3개 + 소환석 5. 첫 화력을 들고
        ///     시작하고, 첫 판단은 유지·계열 강화·파괴로 옮긴다. 08-16의 "소환석 19로 시작"은 되돌린다.
        /// </summary>
        public const int SWARM_STARTING_ORB_GRANT_COUNT = 3;
        public const int SWARM_STARTING_STONE_GRANT = 5;

        /// <summary>
        ///     오브 파괴 환급 (#232 4단계): 계열 공유 레벨은 플레이어에게 귀속되므로 표시 티어와
        ///     무관하게 오브 한 개당 소환석 1로 고정한다 — 강화한 오브를 부숴도 레벨은 남는다.
        /// </summary>
        public const int SWARM_ORB_DESTROY_REFUND_STONES = 1;

        /// <summary>결정 패킷 액션 — 클라·서버·로그 공유. TargetItemUid = OrbColor 값.</summary>
        public const int SWARM_ORB_DECISION_FAMILY_UPGRADE = 1;

        /// <summary>
        /// Survivor Royale P0에서는 레거시 마니또 체크리스트를 생성하거나 진행하지 않는다.
        /// 데이터와 프로토콜은 보존하므로 레거시 모드가 다시 분리되면 이 게이트로 복구할 수 있다.
        /// </summary>
        public static readonly bool CHECKLIST_SYSTEM_ENABLED = false;

        /// <summary>
        ///     프레즌스(기척 카드·북마크·노트북) 동결 플래그 — 차기 재사용 보존 결정(2026-08-20).
        ///     꺼져 있으면 서버 프레즌스 틱과 북마크 핸들러 등록을 건너뛴다. 데이터·프로토콜·클라 수신은 보존.
        /// </summary>
        public static readonly bool PRESENCE_SYSTEM_ENABLED = false;
    }
}
