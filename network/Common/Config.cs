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

        /// <summary>열쇠 (#222 M4): 무료 소환 1회 충전 — 탈주 고블린(미니보스) 드랍. 사람 전용.</summary>
        public const int KEY_GROUND_ITEM_ID = 107000090;

        // 상태 효과 표시 ID (status_effect_info.csv와 동기)
        public const int BOOTS_STATUS_EFFECT_ID = 1101;
        public const int KEY_STATUS_EFFECT_ID = 1102;
        public const int BARE_STATUS_EFFECT_ID = 1103;

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
        public const int SWARM_ORB_CAPACITY = 99;

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
        /// Survivor Royale P0에서는 레거시 마니또 체크리스트를 생성하거나 진행하지 않는다.
        /// 데이터와 프로토콜은 보존하므로 레거시 모드가 다시 분리되면 이 게이트로 복구할 수 있다.
        /// </summary>
        public static readonly bool CHECKLIST_SYSTEM_ENABLED = false;
    }
}
